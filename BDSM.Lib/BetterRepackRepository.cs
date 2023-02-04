using System.Collections.Immutable;
using System.Numerics;
using static System.Convert;

using FluentFTP;

using static BDSM.Lib.Configuration;

namespace BDSM.Lib;
public static class BetterRepackRepositoryDefinitions
{
	public const string MainModpackName = "Sideloader Modpack";
	public const string MEShadersModpackName = "Sideloader Modpack - MaterialEditor Shaders";
	public const string UncensorSelectorModpackName = "Sideloader Modpack - Uncensor Selector";
	public const string ExclusiveHS2ModpackName = "Sideloader Modpack - Exclusive HS2";
	public const string ExclusiveAISModpackName = "Sideloader Modpack - Exclusive AIS";
	public const string StudioMapsModpackName = "Sideloader Modpack - Maps";
	public const string HS2MapsModpackName = "Sideloader Modpack - Maps (HS2 Game)";
	public const string BleedingEdgeModpackName = "Sideloader Modpack - Bleeding Edge";
	public const string StudioModpackName = "Sideloader Modpack - Studio";
	public const string UserDataHS2ModpackName = "UserData (HS2)";
	public const string UserDataAISModpackName = "UserData (AIS)";
	public const string UserDataDirectoryName = "UserData";
	public static IEnumerable<string> DefaultModpackNames(bool is_hs2)
	{
		HashSet<string> desired_modpack_names = CommonModpacks.ToHashSet();
		_ = desired_modpack_names.Add(ExclusiveModpack(is_hs2));
		if (is_hs2)
		{
			_ = desired_modpack_names.Add(HS2MapsModpackName);
			_ = desired_modpack_names.Add(UserDataHS2ModpackName);
		}
		else
			_ = desired_modpack_names.Add(UserDataAISModpackName);
		return desired_modpack_names;
	}
	public static readonly ImmutableDictionary<string, ModpackDefinition> AllBasePathMappings = new Dictionary<string, ModpackDefinition>()
	{
		{ MainModpackName, new ModpackDefinition
			{
				Name = MainModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack",
				LocalRelativePath = "mods\\Sideloader Modpack",
				DeleteClientFiles = true
			}
		},
		{ ExclusiveHS2ModpackName, new ModpackDefinition
			{
				Name = ExclusiveHS2ModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - Exclusive HS2",
				LocalRelativePath = "mods\\Sideloader Modpack - Exclusive HS2",
				DeleteClientFiles = true
			}
		},
		{ ExclusiveAISModpackName, new ModpackDefinition
			{
				Name = ExclusiveAISModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - Exclusive AIS",
				LocalRelativePath = "mods\\Sideloader Modpack - Exclusive AIS",
				DeleteClientFiles = true
			}
		},
		{ StudioMapsModpackName, new ModpackDefinition
			{
				Name = StudioMapsModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - Maps",
				LocalRelativePath = "mods\\Sideloader Modpack - Maps",
				DeleteClientFiles = true
			}
		},
		{ HS2MapsModpackName, new ModpackDefinition
			{
				Name = HS2MapsModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - Maps (HS2 Game)",
				LocalRelativePath = "mods\\Sideloader Modpack - Maps (HS2 Game)",
				DeleteClientFiles = true
			}
		},
		{ MEShadersModpackName, new ModpackDefinition
			{
				Name = MEShadersModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - MaterialEditor Shaders",
				LocalRelativePath = "mods\\Sideloader Modpack - MaterialEditor Shaders",
				DeleteClientFiles = true
			}
		},
		{ StudioModpackName, new ModpackDefinition
			{
				Name = StudioModpackName,
				RemoteRelativePath = "mods/Sideloader Modpack - Studio",
				LocalRelativePath = "mods\\Sideloader Modpack - Studio",
				DeleteClientFiles = true
			}
		},
		{ BleedingEdgeModpackName, new ModpackDefinition
			{
				Name = BleedingEdgeModpackName,
				RemoteRelativePath = "mods/SideloaderModpack-BleedingEdge",
				LocalRelativePath = "mods\\Sideloader Modpack - Bleeding Edge",
				DeleteClientFiles = true
			}
		},
		{ UncensorSelectorModpackName, new ModpackDefinition
			{
				Name = UncensorSelectorModpackName,
				RemoteRelativePath = "mods/SideloaderModpack-UncensorSelector",
				LocalRelativePath = "mods\\Sideloader Modpack - Uncensor Selector",
				DeleteClientFiles = true
			}
		},
		{ UserDataHS2ModpackName, new ModpackDefinition
			{
				Name = UserDataHS2ModpackName,
				RemoteRelativePath = "UserData-HS2",
				LocalRelativePath = "UserData",
				DeleteClientFiles = false
			}
		},
		{ UserDataAISModpackName, new ModpackDefinition
			{
				Name = UserDataAISModpackName,
				RemoteRelativePath = "UserData-AIS",
				LocalRelativePath = "UserData",
				DeleteClientFiles = false
			}
		}
	}.ToImmutableDictionary();
	public static readonly ImmutableHashSet<string> CommonModpacks = new HashSet<string> { "Sideloader Modpack", "Sideloader Modpack - MaterialEditor Shaders", "Sideloader Modpack - Uncensor Selector" }.ToImmutableHashSet();
	public static string UserDataModpackName(bool is_hs2) => is_hs2 ? UserDataHS2ModpackName : UserDataAISModpackName;
	public static string ExclusiveModpack(bool is_hs2) => is_hs2 ? ExclusiveHS2ModpackName : ExclusiveAISModpackName;

	public static readonly List<RepoConnectionInfo> RepoConnectionInfos = new()
	{	new()
		{
			Address = Nice("ɊɒȖɜɑɘɞɓɘȗɌɘɖ"), //ais
			Username = Nice("ɜɒɍɎɕɘɊɍɎɛ"),
			Password = Nice("ɜɒɍɎɕɘɊɍɎɛȜ"),
			Port = Nice<int>("țȚțȚ"),
			RootPath = Nice("ȘȪȲȘ"),
			MaxConnections = Nice<int>("Ȝ"),
		},
		new()
		{
			Address = Nice("ɋɎɝɝɎɛɛɎəɊɌɔȗɌɘɖ"), //br
			Username = Nice("ɜɒɍɎɕɘɊɍɎɛ"),
			Password = Nice("ɜɒɍɎɕɘɊɍɎɛȜ"),
			Port = Nice<int>("țȚțȚ"),
			RootPath = Nice("ȘȪȲȘ"),
			MaxConnections = Nice<int>("Ȝ"),
		},
		new()
		{
			Address = Nice("ɎɞɜțȗɋɎɝɝɎɛɛɎəɊɌɔȗɌɘɖ"), //e2
			Username = Nice("ɜɒɍɎɕɘɊɍɎɛ"),
			Password = Nice("ɜɒɍɎɕɘɊɍɎɛȜ"),
			Port = Nice<int>("țȚțȚ"),
			RootPath = Nice("ȘȪȲȘ"),
			MaxConnections = Nice<int>("Ȝ"),
		},
		new()
		{
			Address = Nice("ɎɞɜȜȗɋɎɝɝɎɛɛɎəɊɌɔȗɌɘɖ"), //e3
			Username = Nice("ɜɒɍɎɕɘɊɍɎɛ"),
			Password = Nice("ɜɒɍɎɕɘɊɍɎɛȜ"),
			Port = Nice<int>("țȚțȚ"),
			RootPath = Nice("ȘȪȲȘ"),
			MaxConnections = Nice<int>("Ȝ"),
		},
		new()
		{
			Address = Nice("ɜɒɍɎɕɘɊɍȗɋɎɝɝɎɛɛɎəɊɌɔȗɌɘɖ"), //side
			Username = Nice("ɜɒɍɎɕɘɊɍɎɛ"),
			Password = Nice("ɜɒɍɎɕɘɊɍɎɛȜ"),
			Port = Nice<int>("țȚțȚ"),
			RootPath = Nice("ȘȪȲȘ"),
			MaxConnections = Nice<int>("Ȝ"),
		},
	};
	static readonly Random RandomRepoIdx = new();
	public static readonly RepoConnectionInfo DefaultConnectionInfo = RepoConnectionInfos[RandomRepoIdx.Next(0, 5)];

	public static FtpConfig DefaultRepoConnectionConfig => new()
	{
		EncryptionMode = FtpEncryptionMode.Auto,
		ValidateAnyCertificate = true,
		LogToConsole = false,
		//ConnectTimeout = 1000,
		//DataConnectionType = FtpDataConnectionType.PASV,
		//SocketKeepAlive = true
	};
	public static ImmutableHashSet<string> GetDesiredModpackNames(bool is_hs2, SimpleUserConfiguration.Modpacks desired_modpacks)
	{
		HashSet<string> desired_modpack_names = CommonModpacks.ToHashSet();
		_ = desired_modpack_names.Add(ExclusiveModpack(is_hs2));
		if (is_hs2 && desired_modpacks.HS2Maps) _ = desired_modpack_names.Add(HS2MapsModpackName);
		if (desired_modpacks.Studio) _ = desired_modpack_names.Add(StudioModpackName);
		if (desired_modpacks.StudioMaps) _ = desired_modpack_names.Add(StudioMapsModpackName);
		if (desired_modpacks.BleedingEdge) _ = desired_modpack_names.Add(BleedingEdgeModpackName);
		if (desired_modpacks.Userdata) _ = desired_modpack_names.Add(UserDataModpackName(is_hs2));
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

	public static string Nice(string sixtynine)
	{
		string clear = string.Empty;
		foreach (char c in sixtynine)
			clear += ToChar(ToInt32(c) - (69 + 420));
		return clear;
	}
	public static int Nice<T>(string sixtynine) where T : IBinaryInteger<int>
	{
		string clear = string.Empty;
		foreach (char c in sixtynine)
			clear += ToChar(ToInt32(c) - (69 + 420));
		return ToInt32(clear);
	}
	public static string SixtyNine(string nice)
	{
		string sixtynine = string.Empty;
		foreach (char c in nice)
			sixtynine += ToChar(ToInt32(c) + 69 + 420);
		return sixtynine;
	}
	public static string SixtyNine(int nice)
	{
		string sixtynine = string.Empty;
		foreach (char c in nice.ToString())
			sixtynine += ToChar(c + 69 + 420);
		return sixtynine;
	}
}
