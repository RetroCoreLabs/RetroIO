//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Reflection;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Guards the SignalR push contract shared by the management API and its clients.
/// </summary>
/// <remarks>
/// The server pushes through the typed <see cref="IServerHubClient"/>, so the compiler already
/// checks the server side. Clients subscribe by wire NAME through
/// <see cref="ServerHubMethods"/>, because SignalR's HubConnection.On overloads take a string
/// rather than a method group -- and a string is exactly what the compiler cannot check. These
/// tests close that gap: rename a method on the interface without updating the constant and the
/// build stays green but the run fails here.
/// </remarks>
[TestFixture]
public class ServerHubContractTests
{
    /// <summary>Every wire-name constant must name a real method on the hub interface.</summary>
    [Test]
    public void EveryHubMethodConstant_NamesAMethodOnTheInterface()
    {
        FieldInfo[] constants = typeof(ServerHubMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        int checkedCount = 0;

        for (int i = 0; i < constants.Length; i++)
        {
            FieldInfo field = constants[i];

            // HubPath is the endpoint route, not a push name; it is deliberately not a method.
            if (field.Name == nameof(ServerHubMethods.HubPath)) continue;

            string wireName = (string)field.GetRawConstantValue()!;

            Assert.That(typeof(IServerHubClient).GetMethod(wireName), Is.Not.Null,
                $"ServerHubMethods.{field.Name} is \"{wireName}\", but IServerHubClient has no " +
                "method of that name. A client subscribing to it would never receive anything.");

            Assert.That(wireName, Is.EqualTo(field.Name),
                $"ServerHubMethods.{field.Name} should hold its own name as the wire name, " +
                "so the constant and the interface method cannot drift apart.");

            checkedCount++;
        }

        Assert.That(checkedCount, Is.GreaterThan(0), "no push-name constants were checked");
    }

    /// <summary>Every push on the hub interface must have a wire-name constant clients can use.</summary>
    [Test]
    public void EveryInterfaceMethod_HasAWireNameConstant()
    {
        MethodInfo[] methods = typeof(IServerHubClient).GetMethods();

        Assert.That(methods.Length, Is.GreaterThan(0), "IServerHubClient declares no pushes");

        for (int i = 0; i < methods.Length; i++)
        {
            string name = methods[i].Name;

            FieldInfo? constant = typeof(ServerHubMethods).GetField(
                name, BindingFlags.Public | BindingFlags.Static);

            Assert.That(constant, Is.Not.Null,
                $"IServerHubClient.{name} has no ServerHubMethods constant, so a client cannot " +
                "subscribe to it without hardcoding the string.");
        }
    }

    /// <summary>
    /// The contract assembly must stay dependency-free, so any client can reference it.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the assembly exists: RetroServeUI publishes self-contained and
    /// could not reference the API host (NETSDK1150). If someone adds an ASP.NET or RetroFS
    /// dependency here, that lands right back on every client, so fail loudly instead.
    /// </remarks>
    [Test]
    public void ContractsAssembly_ReferencesNothingButTheFramework()
    {
        AssemblyName[] references = typeof(IServerHubClient).Assembly.GetReferencedAssemblies();

        for (int i = 0; i < references.Length; i++)
        {
            string name = references[i].Name ?? string.Empty;

            bool isFramework = name.StartsWith("System.", StringComparison.Ordinal) ||
                               name == "System" ||
                               name == "netstandard" ||
                               name == "mscorlib";

            Assert.That(isFramework, Is.True,
                $"RetroIO.Management.Contracts references '{name}'. It must depend on nothing " +
                "but the base framework so that every client can reference it freely.");
        }
    }
}
