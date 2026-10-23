using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin;
using Ancela.Agent.SemanticKernel.Plugins.MemoryPlugin.Models;
using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;
using Newtonsoft.Json;

namespace Ancela.Agent.Tests;

/// <summary>
/// A to-do can carry the day it must be done by. Ancela doesn't remind anyone of it; get_todos lists
/// dated to-dos first with the days left in the user's own calendar, so a morning briefing (a
/// scheduled task) can warn when one is getting close.
/// </summary>
public class TodoDueDateTests
{
    private const string AgentPhoneNumber = "+15550000000";
    private const string Pacific = "America/Los_Angeles";

    private readonly Mock<IMemoryClient> _memory = new();
    private readonly MemoryPlugin _plugin;
    private readonly Kernel _kernel = new();

    public TodoDueDateTests()
    {
        Environment.SetEnvironmentVariable("TWILIO_PHONE_NUMBER", "+10000000000");
        Environment.SetEnvironmentVariable("TWILIO_ACCOUNT_SID", "ACXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        Environment.SetEnvironmentVariable("TWILIO_AUTH_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("OWNER_PHONE_NUMBER", "+15551234567");
        _plugin = new MemoryPlugin(_memory.Object, new SmsService(), new OwnerService());
        _kernel.Data["agentPhoneNumber"] = AgentPhoneNumber;
        _kernel.Data["userPhoneNumber"] = "+15551234567";
        _kernel.Data["userTimeZone"] = Pacific;
    }

    private static DateOnly TodayIn(string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    private static ToDoModel Todo(string content, DateOnly? due = null, int createdDaysAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Content = content,
        Created = DateTimeOffset.UtcNow.AddDays(-createdDaysAgo),
        Deleted = null,
        Due = due,
    };

    [Fact]
    public async Task SaveTodo_StoresTheDueDate()
    {
        await _plugin.SaveToDoAsync(_kernel, "Renew vehicle tags", "2026-11-15");

        _memory.Verify(m => m.SaveToDoAsync(AgentPhoneNumber, "+15551234567", "Renew vehicle tags", new DateOnly(2026, 11, 15)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SaveTodo_WithoutADate_HasNoDueDate(string? dueDate)
    {
        await _plugin.SaveToDoAsync(_kernel, "Buy milk", dueDate);

        _memory.Verify(m => m.SaveToDoAsync(AgentPhoneNumber, "+15551234567", "Buy milk", null));
    }

    [Theory]
    [InlineData("Nov 15")]
    [InlineData("11/15/2026")]
    [InlineData("2026-02-30")]
    public async Task SaveTodo_WithADateItCantRead_SavesNothing(string dueDate)
    {
        var act = () => _plugin.SaveToDoAsync(_kernel, "Renew vehicle tags", dueDate);

        await act.Should().ThrowAsync<ArgumentException>(because: "a guessed date is worse than an error the model can correct");
        _memory.Verify(m => m.SaveToDoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>()), Times.Never);
    }

    [Theory]
    [InlineData("2026-12-01", 2026, 12, 1)]
    [InlineData(null, null, null, null)]
    public async Task SetDueDate_SetsOrRemovesIt(string? dueDate, int? year, int? month, int? day)
    {
        var id = Guid.NewGuid();

        await _plugin.SetToDoDueDateAsync(_kernel, id, dueDate);

        DateOnly? expected = year is null ? null : new DateOnly(year.Value, month!.Value, day!.Value);
        _memory.Verify(m => m.SetToDoDueDateAsync(id, AgentPhoneNumber, expected));
    }

    [Fact]
    public async Task GetTodos_ListsDatedFirst_SoonestFirst_WithTheDaysLeft_ThenTheRestOldestFirst()
    {
        var today = TodayIn(Pacific);
        _memory.Setup(m => m.GetToDosAsync(AgentPhoneNumber)).ReturnsAsync(
        [
            Todo("Newer, no date", createdDaysAgo: 1),
            Todo("File taxes", today.AddDays(60)),
            Todo("Older, no date", createdDaysAgo: 5),
            Todo("Renew vehicle tags", today.AddDays(10)),
            Todo("Return library book", today.AddDays(-2)),
            Todo("Pay the water bill", today),
        ]);

        var todos = await _plugin.GetToDosAsync(_kernel);

        todos.Select(t => (t.Content, t.DaysUntilDue)).Should().Equal(
            ("Return library book", -2),
            ("Pay the water bill", 0),
            ("Renew vehicle tags", 10),
            ("File taxes", 60),
            ("Older, no date", null),
            ("Newer, no date", (int?)null));
    }

    [Fact]
    public async Task GetTodos_CountsDaysInTheUsersCalendar()
    {
        // At any moment these two zones, 26 hours apart, are on different dates.
        var due = TodayIn("Pacific/Kiritimati");
        _memory.Setup(m => m.GetToDosAsync(AgentPhoneNumber)).ReturnsAsync([Todo("Renew vehicle tags", due)]);

        _kernel.Data["userTimeZone"] = "Pacific/Kiritimati";
        var there = (await _plugin.GetToDosAsync(_kernel)).Single();
        _kernel.Data["userTimeZone"] = "Etc/GMT+12";
        var behind = (await _plugin.GetToDosAsync(_kernel)).Single();

        there.DaysUntilDue.Should().Be(0);
        behind.DaysUntilDue.Should().Be(due.DayNumber - TodayIn("Etc/GMT+12").DayNumber).And.BeGreaterThan(0);
    }

    [Fact]
    public void StoredDueDate_ReadsBackThroughCosmosSerializer_AndIsAbsentOnOlderTodos()
    {
        // Cosmos reads documents with Newtonsoft and its default settings; MemoryClient writes the
        // date as "yyyy-MM-dd". To-dos saved before due dates existed have no "due" at all.
        var dated = JsonConvert.DeserializeObject<ToDoModel>(
            """{"id":"7d1f6c1e-1111-4e3a-9d7a-000000000001","content":"Renew vehicle tags","created":"2026-10-22T21:00:00-07:00","deleted":null,"due":"2026-11-15"}""");
        var older = JsonConvert.DeserializeObject<ToDoModel>(
            """{"id":"7d1f6c1e-1111-4e3a-9d7a-000000000002","content":"Buy milk","created":"2026-10-01T09:00:00-07:00","deleted":null}""");

        dated!.Due.Should().Be(new DateOnly(2026, 11, 15));
        older!.Due.Should().BeNull();
    }
}
