using System.Collections.Immutable;

using static BDSM.Lib.Configuration;
using static BDSM.Lib.BetterRepackRepositoryDefinitions;
using BDSM.Lib;

namespace BDSM.Tests;
public static class ConfigurationTestData
{
	public const string ValidGamePath = @"D:\HoneySelect 2 DX";
	public const string InvalidGamePath = @"A:\DoesntExist";

	public static readonly RawUserConfiguration.Modpacks DefaultModpacksRaw = new();

	public static readonly ImmutableHashSet<PathMapping> DefaultPathMappingsAIS = ModpackNamesToPathMappings(DefaultModpackNames(false), ValidGamePath, DefaultConnectionInfo.RootPath);
	public static readonly ImmutableHashSet<PathMapping> DefaultPathMappingsHS2 = ModpackNamesToPathMappings(DefaultModpackNames(true), ValidGamePath, DefaultConnectionInfo.RootPath);

	public const string v032YAMLAllPacks =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Main: false
  MEShaders: true
  UncensorSelector: true
  Exclusive: true
  StudioMaps: true
  HS2Maps: true
  Studio: true
  BleedingEdge: true
  Userdata: true
PromptToContinue: true
";
	public const string v032YAMLDefaultPacks =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Main: true
  MEShaders: true
  UncensorSelector: true
  Exclusive: true
  StudioMaps: false
  HS2Maps: true
  Studio: false
  BleedingEdge: false
  Userdata: true
PromptToContinue: true
";
	public const string v032YAMLNonDefault =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Main: false
  MEShaders: true
  UncensorSelector: true
  Exclusive: true
  StudioMaps: true
  HS2Maps: true
  Studio: true
  BleedingEdge: true
  Userdata: true
PromptToContinue: true
";
	public const string v032YAMLBadGamePath =
	$@"GamePath: {InvalidGamePath}
OptionalModpacks:
  Main: true
  MEShaders: true
  UncensorSelector: true
  Exclusive: true
  StudioMaps: true
  HS2Maps: true
  Studio: true
  BleedingEdge: true
  Userdata: true
PromptToContinue: true
";
	public const string v032YAMLMalformed =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
Main: true
MEShaders: true
UncensorSelector: true
  Exclusive: true
  StudioMaps: true
  HS2Maps: true
  Studio: true
  BleedingEdge: true
  Userdata: true
PromptToContinue: true
";
	public const string v03YAMLAllPacks =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Studio: true
  BleedingEdge: true
  Userdata: true
PromptToContinue: true
";
	public const string v03YAMLDefault =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Studio: false
  BleedingEdge: false
  Userdata: true
PromptToContinue: true
";
	public const string v03YAMLMalformed =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
Studio: false
BleedingEdge: false
Userdata: true
PromptToContinue: true
";
	public const string v03YAMLMissing =
	$@"GamePath: {ValidGamePath}
OptionalModpacks:
  Studio: false
  BleedingEdge: false
  Userdata: true
";

	// Not testing v0.1 YAML. Only testing that the upgrade logic works.

	public static readonly string[] OldConfigModpackDefaults = new string[]
	{
		@"mods/Sideloader Modpack | mods\Sideloader Modpack | true",
		@"mods/Sideloader Modpack - Exclusive HS2 | mods\Sideloader Modpack - Exclusive HS2 | true",
		@"mods/Sideloader Modpack - Maps (HS2 Game) | mods\Sideloader Modpack - Maps (HS2 Game) | true",
		@"mods/Sideloader Modpack - MaterialEditor Shaders | mods\Sideloader Modpack - MaterialEditor Shaders | true",
		@"mods/SideloaderModpack-UncensorSelector | mods\Sideloader Modpack - Uncensor Selector | true",
		"UserData-HS2 | UserData | false"
	};
	public static readonly string[] OldConfigModpackFull = new string[]
	{
		@"mods/Sideloader Modpack | mods\Sideloader Modpack | true",
		@"mods/Sideloader Modpack - Exclusive HS2 | mods\Sideloader Modpack - Exclusive HS2 | true",
		@"mods/Sideloader Modpack - Maps | mods\Sideloader Modpack - Maps | true",
		@"mods/Sideloader Modpack - Maps (HS2 Game) | mods\Sideloader Modpack - Maps (HS2 Game) | true",
		@"mods/Sideloader Modpack - MaterialEditor Shaders | mods\Sideloader Modpack - MaterialEditor Shaders | true",
		@"mods/Sideloader Modpack - Studio | mods\Sideloader Modpack - Studio | true",
		@"mods/SideloaderModpack-BleedingEdge | mods\Sideloader Modpack - Bleeding Edge | true",
		@"mods/SideloaderModpack-UncensorSelector | mods\Sideloader Modpack - Uncensor Selector | true",
		"UserData-HS2 | UserData | false"
	};
	public static readonly string[] OldConfigModpackNonDefault = new string[]
	{
		@"mods/Sideloader Modpack | mods\Sideloader Modpack | true",
		@"mods/Sideloader Modpack - Exclusive HS2 | mods\Sideloader Modpack - Exclusive HS2 | true",
		@"mods/Sideloader Modpack - Maps (HS2 Game) | mods\Sideloader Modpack - Maps (HS2 Game) | true",
		@"mods/Sideloader Modpack - MaterialEditor Shaders | mods\Sideloader Modpack - MaterialEditor Shaders | true",
		@"mods/Sideloader Modpack - Studio | mods\Sideloader Modpack - Studio | true",
		@"mods/SideloaderModpack-UncensorSelector | mods\Sideloader Modpack - Uncensor Selector | true",
		"UserData-HS2 | UserData | false"
	};

	public static readonly OldUserConfiguration OldUserConfigurationNormal = new()
    {
        GamePath = ValidGamePath,
        ConnectionInfo = DefaultConnectionInfo,
        BaseSideloaderDirectories = OldConfigModpackDefaults,
        PromptToContinue = true
    };
	public static readonly OldUserConfiguration OldUserConfigurationNonDefault = new()
    {
        GamePath = ValidGamePath,
        ConnectionInfo = DefaultConnectionInfo,
        BaseSideloaderDirectories = OldConfigModpackNonDefault,
        PromptToContinue = true
    };
	public static readonly OldUserConfiguration OldUserConfigurationBadGamePath = new()
    {
        GamePath = InvalidGamePath,
        ConnectionInfo = DefaultConnectionInfo,
        BaseSideloaderDirectories = OldConfigModpackDefaults,
        PromptToContinue = true
    };
	public static readonly OldUserConfiguration OldUserConfigurationCustomCI = new()
    {
        GamePath = ValidGamePath,
        ConnectionInfo = new()
        {
        Address = "other.place.com",
        Username = "user",
        Password = "pass",
        PasswordB64 = null,
        MaxConnections = 4,
        Port = 21,
        RootPath = "/mods/"
        },
        BaseSideloaderDirectories = OldConfigModpackDefaults,
        PromptToContinue = true
    };

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
	// YamlDotNet is stupid and is happy to force GamePath to be set to null even though it's non-nullable, so test for it
	public static readonly RawUserConfiguration RawUserConfigurationEmpty = new() { GamePath = null, OptionalModpacks = null, PromptToContinue = null };
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
	public static readonly RawUserConfiguration RawUserConfigurationGamePathOnly = new() { GamePath = ValidGamePath };
	public static readonly RawUserConfiguration RawUserConfigurationBadGamePath = new() { GamePath = InvalidGamePath };
	public static readonly RawUserConfiguration RawUserConfigurationDefault = new() { GamePath = ValidGamePath, OptionalModpacks = DefaultModpacksRawHS2, PromptToContinue = DefaultPromptToContinue };
	public static readonly RawUserConfiguration RawUserConfigurationCustom = RawUserConfigurationDefault with { OptionalModpacks = DefaultModpacksRawHS2 with { MEShaders = false, Exclusive = false } };
	public static readonly RawUserConfiguration RawUserConfiguration03Config = RawUserConfigurationDefault with { OptionalModpacks = DefaultModpacksRawHS2 with { Main = null, MEShaders = null, Exclusive = null, UncensorSelector = null, HS2Maps = null, StudioMaps = null } };

	public static readonly FullUserConfiguration DefaultFullUserConfigurationHS2 = new()
	{
		GamePath = ValidGamePath,
		BasePathMappings = DefaultPathMappingsHS2,
		ConnectionInfo = DefaultConnectionInfo,
		PromptToContinue = true
	};
	public static readonly FullUserConfiguration DefaultFullUserConfigurationAIS = new()
	{
		GamePath = ValidGamePath,
		BasePathMappings = DefaultPathMappingsAIS,
		ConnectionInfo = DefaultConnectionInfo,
		PromptToContinue = true
	};
}
