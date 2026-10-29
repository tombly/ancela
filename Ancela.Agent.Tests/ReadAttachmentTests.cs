using System.IO.Compression;
using System.Text;
using Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;
using FluentAssertions;
using Microsoft.SemanticKernel;
using Moq;

namespace Ancela.Agent.Tests;

/// <summary>
/// Unit tests for read_attachment: how an attachment's bytes become text (AttachmentText), and that
/// the tool passes its arguments through. MailSearchLiveTests covers the Graph calls.
/// </summary>
public class ReadAttachmentTests
{
    // The shape of a DMARC aggregate report (RFC 7489 appendix C), cut to one record.
    private const string DmarcReport = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata><org_name>google.com</org_name><report_id>4931150279366017725</report_id></report_metadata>
          <policy_published><domain>example.com</domain><p>reject</p></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.50</source_ip><count>6</count>
              <policy_evaluated><disposition>reject</disposition><dkim>fail</dkim><spf>fail</spf></policy_evaluated>
            </row>
            <identifiers><header_from>example.com</header_from></identifiers>
          </record>
        </feedback>
        """;

    [Fact]
    public void TextFile_IsReadAsItIs()
    {
        var attachment = AttachmentText.Read("report.xml", Encoding.UTF8.GetBytes(DmarcReport));

        attachment.Name.Should().Be("report.xml");
        attachment.Files.Should().ContainSingle();
        attachment.Files[0].Name.Should().Be("report.xml");
        attachment.Files[0].Text.Should().Be(DmarcReport.Replace("\r\n", "\n"));
        attachment.Files[0].Truncated.Should().BeFalse();
        attachment.NotRead.Should().BeEmpty();
    }

    [Fact]
    public void GzipFile_IsUnpacked_AndNamedWithoutTheGz()
    {
        // Microsoft and Yahoo send their DMARC reports as .xml.gz.
        var attachment = AttachmentText.Read("enterprise.protection.outlook.com!example.com!1729900800!1729987200.xml.gz", Gzip(DmarcReport));

        attachment.Files.Should().ContainSingle();
        attachment.Files[0].Name.Should().Be("enterprise.protection.outlook.com!example.com!1729900800!1729987200.xml");
        attachment.Files[0].Text.Should().Contain("<source_ip>203.0.113.50</source_ip>");
    }

    [Fact]
    public void GzipFile_IsRecognizedByItsContent_WhateverItIsCalled()
    {
        AttachmentText.Read("report", Gzip(DmarcReport)).Files[0].Text.Should().Contain("<disposition>reject</disposition>");
    }

    [Fact]
    public void ZipFile_ReadsTheTextFilesInside()
    {
        // Google sends its DMARC reports as a .zip holding one .xml.
        var attachment = AttachmentText.Read("google.com!example.com!1729900800!1729987199.zip",
            Zip(("google.com!example.com!1729900800!1729987199.xml", Encoding.UTF8.GetBytes(DmarcReport))));

        attachment.Files.Should().ContainSingle();
        attachment.Files[0].Name.Should().Be("google.com!example.com!1729900800!1729987199.xml");
        attachment.Files[0].Text.Should().Contain("<count>6</count>");
    }

    [Fact]
    public void ZipFile_SaysWhichFilesItDidNotRead()
    {
        var attachment = AttachmentText.Read("export.zip", Zip(
            ("transactions.csv", "date,amount\n2026-10-01,12.50\n"u8.ToArray()),
            ("logo.png", Png)));

        attachment.Files.Select(f => f.Name).Should().Equal("transactions.csv");
        attachment.NotRead.Should().Equal("logo.png: not a text file");
    }

    [Fact]
    public void ZipFile_SharesOneTextCap_AcrossItsFiles()
    {
        var big = new string('a', AttachmentText.TextCap - 10);
        var attachment = AttachmentText.Read("many.zip", Zip(
            ("one.txt", Encoding.UTF8.GetBytes(big)),
            ("two.txt", Encoding.UTF8.GetBytes(new string('b', 100))),
            ("three.txt", "c"u8.ToArray())));

        attachment.Files.Sum(f => f.Text.Length).Should().Be(AttachmentText.TextCap);
        attachment.Files[1].Text.Should().Be(new string('b', 10));
        attachment.Files[1].Truncated.Should().BeTrue(because: "the model must know it hasn't seen the whole file");
        attachment.NotRead.Should().Equal("three.txt: over the text limit");
    }

    [Fact]
    public void ZipFile_WithNoTextInside_IsRefused()
    {
        var act = () => AttachmentText.Read("photos.zip", Zip(("one.png", Png), ("two.png", Png)));

        act.Should().Throw<InvalidOperationException>().WithMessage("None of the files in photos.zip is text*");
    }

    [Fact]
    public void EmptyZip_IsAnAttachmentWithNoFiles()
    {
        var attachment = AttachmentText.Read("empty.zip", Zip());

        attachment.Files.Should().BeEmpty();
        attachment.NotRead.Should().BeEmpty();
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("invoice.pdf")]
    public void BinaryFile_IsRefused_NotReadAsNoise(string name)
    {
        var bytes = name.EndsWith(".png") ? Png : Pdf;

        var act = () => AttachmentText.Read(name, bytes);

        act.Should().Throw<InvalidOperationException>().WithMessage($"{name} is not a text file*");
    }

    [Fact]
    public void OfficeDocument_IsNotOpenedAsAZip()
    {
        // A .docx is a zip of XML parts; read as one it would be pages of markup.
        var docx = Zip(("word/document.xml", "<w:document><w:body>Hello</w:body></w:document>"u8.ToArray()));

        var act = () => AttachmentText.Read("letter.docx", docx);

        act.Should().Throw<InvalidOperationException>().WithMessage("letter.docx is not a text file*");
    }

    [Fact]
    public void LongFile_IsCut_AndSaysSo()
    {
        var attachment = AttachmentText.Read("log.txt", Encoding.UTF8.GetBytes(new string('x', AttachmentText.TextCap + 500)));

        attachment.Files[0].Text.Length.Should().Be(AttachmentText.TextCap);
        attachment.Files[0].Truncated.Should().BeTrue();
    }

    [Fact]
    public void HugeGzip_IsOnlyUnpackedAsFarAsTheCap()
    {
        // 64 MB of text packs into well under 1 MB; only the first TextCap characters are unpacked.
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = Encoding.ASCII.GetBytes(new string('a', 1 << 20));
            for (var i = 0; i < 64; i++)
                gzip.Write(block);
        }

        var attachment = AttachmentText.Read("bomb.txt.gz", packed.ToArray());

        attachment.Files[0].Text.Length.Should().Be(AttachmentText.TextCap);
        attachment.Files[0].Truncated.Should().BeTrue();
    }

    [Fact]
    public void Utf16File_IsReadByItsByteOrderMark()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("name,amount\nCafé,4.50\n")).ToArray();

        AttachmentText.Read("export.csv", bytes).Files[0].Text.Should().Be("name,amount\nCafé,4.50\n");
    }

    [Fact]
    public void FileThatIsNotUtf8_IsReadAsLatin1()
    {
        // An older Windows export: "Café" in Latin-1 is not valid UTF-8.
        var bytes = Encoding.Latin1.GetBytes("name,amount\r\nCafé,4.50\r\n");

        AttachmentText.Read("export.csv", bytes).Files[0].Text.Should().Be("name,amount\nCafé,4.50\n");
    }

    [Fact]
    public async Task ReadAttachment_PassesTheIdAndNameThrough()
    {
        var graph = new Mock<IGraphClient>();
        graph.Setup(g => g.GetAttachmentAsync("email-1", "report.xml.gz"))
            .ReturnsAsync(AttachmentText.Read("report.xml.gz", Gzip(DmarcReport)));
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new GraphPlugin(graph.Object));

        var result = await kernel.InvokeAsync(kernel.Plugins["GraphPlugin"]["read_attachment"],
            new() { ["emailId"] = "email-1", ["name"] = "report.xml.gz" });

        result.GetValue<AttachmentContentModel>()!.Files[0].Text.Should().Contain("203.0.113.50");
        graph.Verify(g => g.GetAttachmentAsync("email-1", "report.xml.gz"), Times.Once);
    }

    // The first bytes of a PNG and a PDF: binary, with NULs and other control characters.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, .. "\n1 0 obj\n<< /Length 20 /Filter /FlateDecode >>\nstream\n"u8, 0x78, 0x9C, 0x00, 0x01, 0x02, 0x03, 0x1B, 0x05];

    private static byte[] Gzip(string text)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return packed.ToArray();
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var packed = new MemoryStream();
        using (var zip = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }
        return packed.ToArray();
    }
}
