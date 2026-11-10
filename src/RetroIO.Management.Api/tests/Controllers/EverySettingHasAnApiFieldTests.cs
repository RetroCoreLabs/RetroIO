//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Reflection;
using RetroIO.Management.Contracts;
using RetroIO.Security.Firewall;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Core;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// Every key of every settings file the server reads has a field in the management API (Ronny 2026-11-10: "a test
/// that fails when a setting has no field"). A key added to a file without an API field makes this test fail and
/// names it. The other half, that every API field has a box on a RetroAdmin page, is checked in the RetroAdmin tests.
/// </summary>
/// <remarks>
/// A key is matched by the same name in the API type, or by the name given in its rename list. A secret is never sent
/// back, so it is matched by its "is set" and "new value" fields instead.
/// </remarks>
[TestFixture]
public class EverySettingHasAnApiFieldTests
{
    private static IEnumerable<TestCaseData> Files()
    {
        yield return Case(typeof(TnfsServerConfiguration), typeof(TnfsSettingsDto),
            "RootDirectory=RootPath", "EnableUdp=UdpEnabled", "EnableTcp=TcpEnabled", "MaxTcpConnections=MaxConnections");
        yield return Case(typeof(EconetServerConfiguration), typeof(EconetSettingsDto),
            "RootDirectory=RootPath", "EnableUdp=UdpEnabled");
        yield return Case(typeof(EconetDiscConfiguration), typeof(EconetDiscDto));
        yield return Case(typeof(EconetMachineWireProfile), typeof(EconetWireProfileOverrideDto));
        yield return Case(typeof(DriveWireServerConfiguration), typeof(DriveWireSettingsDto));
        yield return Case(typeof(DriveWireDriveConfiguration), typeof(DriveWireDriveSettingDto));
        yield return Case(typeof(XmsgHubServerConfiguration), typeof(XmsgHubSettingsDto));
        yield return Case(typeof(XmsgNodeServerConfiguration), typeof(XmsgNodeSettingsDto),
            "ProgramToken=ProgramTokenSet+NewProgramToken");
        yield return Case(typeof(SecurityServerConfiguration), typeof(SecuritySettingsDto));
        yield return Case(typeof(FirewallConfiguration), typeof(FirewallConfigDto));
        yield return Case(typeof(UnifiedMountEntry), typeof(MountDto), "Password=PasswordSet+NewPassword");
    }

    private static TestCaseData Case(Type file, Type api, params string[] renames)
    {
        return new TestCaseData(file, api, renames).SetName("EveryKeyOf_" + file.Name + "_IsIn_" + api.Name);
    }

    /// <summary>
    /// Every public settable property of the file type is in the API type.
    /// </summary>
    /// <param name="file">The type the file is read into.</param>
    /// <param name="api">The type the API sends.</param>
    /// <param name="renames">"FileName=ApiName" pairs; "+" joins several API names that together carry one key.</param>
    [TestCaseSource(nameof(Files))]
    public void EveryKey_HasAnApiField(Type file, Type api, string[] renames)
    {
        List<string> missing = new List<string>();
        PropertyInfo[] keys = file.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < keys.Length; i++)
        {
            if (!keys[i].CanWrite)
            {
                continue;
            }

            string[] wanted = new[] { keys[i].Name };
            for (int r = 0; r < renames.Length; r++)
            {
                string[] pair = renames[r].Split('=');
                if (pair[0] == keys[i].Name)
                {
                    wanted = pair[1].Split('+');
                }
            }

            for (int w = 0; w < wanted.Length; w++)
            {
                if (api.GetProperty(wanted[w], BindingFlags.Public | BindingFlags.Instance) == null)
                {
                    missing.Add(file.Name + "." + keys[i].Name + " (looked for " + api.Name + "." + wanted[w] + ")");
                }
            }
        }

        Assert.That(missing, Is.Empty, "Settings with no field in the management API");
    }
}
