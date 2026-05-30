using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ancela.Cli.Infrastructure;

/// <summary>
/// Bridges Spectre.Console.Cli's DI abstractions onto a standard
/// <see cref="IServiceCollection"/> so commands can take constructor dependencies.
/// </summary>
public sealed class TypeRegistrar(IServiceCollection builder) : ITypeRegistrar
{
    private IServiceProvider? _provider;

    /// <summary>The single built provider, shared between the CommandApp and the interactive shell.</summary>
    public IServiceProvider Provider => _provider ??= builder.BuildServiceProvider();

    public ITypeResolver Build() => new TypeResolver(Provider);

    public void Register(Type service, Type implementation) =>
        builder.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        builder.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        builder.AddSingleton(service, _ => factory());
}

/// <summary>
/// Resolves over a provider it does not own. The provider is shared across the CommandApp
/// (re-invoked per REPL line) and the shell, so disposal is the registrar/host's job — not
/// something that should happen after each command run.
/// </summary>
public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver
{
    public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);
}
