using System.Collections.Immutable;

using FluentFTP;

using static BDSM.Configuration;

namespace BDSM;
public static class BetterRepackRepositoryDefinitions
{
	public static readonly ImmutableDictionary<string, ModpackDefinition> AllBasePathMappings = new Dictionary<string, ModpackDefinition>()
	{
		{ "Sideloader Modpack", new ModpackDefinition
			{
				Name = "Sideloader Modpack",
				RemoteRelativePath = "mods/Sideloader Modpack",
				LocalRelativePath = "mods\\Sideloader Modpack",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Exclusive HS2", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Exclusive HS2",
				RemoteRelativePath = "mods/Sideloader Modpack - Exclusive HS2",
				LocalRelativePath = "mods\\Sideloader Modpack - Exclusive HS2",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Exclusive AIS", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Exclusive AIS",
				RemoteRelativePath = "mods/Sideloader Modpack - Exclusive AIS",
				LocalRelativePath = "mods\\Sideloader Modpack - Exclusive AIS",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Maps", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Maps",
				RemoteRelativePath = "mods/Sideloader Modpack - Maps",
				LocalRelativePath = "mods\\Sideloader Modpack - Maps",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Maps (HS2 Game)", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Maps (HS2 Game)",
				RemoteRelativePath = "mods/Sideloader Modpack - Maps (HS2 Game)",
				LocalRelativePath = "mods\\Sideloader Modpack - Maps (HS2 Game)",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - MaterialEditor Shaders", new ModpackDefinition
			{
				Name = "Sideloader Modpack - MaterialEditor Shaders",
				RemoteRelativePath = "mods/Sideloader Modpack - MaterialEditor Shaders",
				LocalRelativePath = "mods\\Sideloader Modpack - MaterialEditor Shaders",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Studio", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Studio",
				RemoteRelativePath = "mods/Sideloader Modpack - Studio",
				LocalRelativePath = "mods\\Sideloader Modpack - Studio",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Bleeding Edge", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Bleeding Edge",
				RemoteRelativePath = "mods/SideloaderModpack-BleedingEdge",
				LocalRelativePath = "mods\\Sideloader Modpack - Bleeding Edge",
				DeleteClientFiles = true
			}
		},
		{ "Sideloader Modpack - Uncensor Selector", new ModpackDefinition
			{
				Name = "Sideloader Modpack - Uncensor Selector",
				RemoteRelativePath = "mods/SideloaderModpack-UncensorSelector",
				LocalRelativePath = "mods\\Sideloader Modpack - Uncensor Selector",
				DeleteClientFiles = true
			}
		},
		{ "UserData (HS2)", new ModpackDefinition
			{
				Name = "UserData (HS2)",
				RemoteRelativePath = "UserData-HS2",
				LocalRelativePath = "UserData",
				DeleteClientFiles = false
			}
		},
		{ "UserData (AIS)", new ModpackDefinition
			{
				Name = "UserData (AIS)",
				RemoteRelativePath = "UserData-AIS",
				LocalRelativePath = "UserDataAIS",
				DeleteClientFiles = false
			}
		}
	}.ToImmutableDictionary();
	public static readonly ImmutableHashSet<string> CommonModpacks = new HashSet<string> { "Sideloader Modpack", "Sideloader Modpack - Maps", "Sideloader Modpack - MaterialEditor Shaders", "Sideloader Modpack - Uncensor Selector" }.ToImmutableHashSet();
	public const string BleedingEdgeModpackName = "Sideloader Modpack - Bleeding Edge";
	public const string StudioModpackName = "Sideloader Modpack - Studio";
	public const string UserDataHS2ModpackName = "UserData (HS2)";
	public const string UserDataAISModpackName = "UserData (AIS)";
	public const string UserDataDirectoryName = "UserData";
	public static string UserDataModpackName(bool is_hs2) => is_hs2 ? UserDataHS2ModpackName : UserDataAISModpackName;
	public static ImmutableHashSet<string> ExclusiveModpacks(bool is_hs2) => is_hs2 ?
		new HashSet<string> { "Sideloader Modpack - Exclusive HS2", "Sideloader Modpack - Maps (HS2 Game)" }.ToImmutableHashSet() : new HashSet<string> { "Sideloader Modpack - Exclusive AIS" }.ToImmutableHashSet();

	public static readonly RepoConnectionInfo DefaultConnectionInfo = new()
	{
		Address = "sideload.betterrepack.com",
		Username = "sideloader",
		Password = null,
		PasswordB64 = "c2lkZWxvYWRlcjM =",
		Port = 2121,
		RootPath = "/AI/",
		MaxConnections = 5
	};
	public static FtpConfig DefaultRepoConnectionConfig => new()
	{
		EncryptionMode = FtpEncryptionMode.Auto,
		ValidateAnyCertificate = true,
		LogToConsole = false,
	};
	public static ImmutableHashSet<string> GetDesiredModpackNames(bool is_hs2, bool studio, bool bleedingedge, bool userdata)
	{
		HashSet<string> desired_modpack_names = CommonModpacks.ToHashSet();
		desired_modpack_names.UnionWith(ExclusiveModpacks(is_hs2));
		if (userdata)
			_ = desired_modpack_names.Add(UserDataModpackName(is_hs2));
		if (studio)
			_ = desired_modpack_names.Add(StudioModpackName);
		if (bleedingedge)
			_ = desired_modpack_names.Add(BleedingEdgeModpackName);
		return desired_modpack_names.ToImmutableHashSet();
	}
	public static ImmutableHashSet<PathMapping> ModpackNamesToPathMappings(IEnumerable<string> modpack_names, string gamepath, string rootpath)
	{
		HashSet<PathMapping> modpack_pathmaps = new();
		foreach (string modpack_name in modpack_names)
		{
			ModpackDefinition definition = AllBasePathMappings[modpack_name];
			_ = modpack_pathmaps.Add(ModpackDefinitionToPathMapping(definition, gamepath, rootpath));
		}
		return modpack_pathmaps.ToImmutableHashSet();
	}
}
