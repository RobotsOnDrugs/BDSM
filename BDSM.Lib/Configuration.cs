using System.Collections.Immutable;
using System.Text;

using NLog;

using YamlDotNet.Core;
using YamlDotNet.Serialization;

using static BDSM.Lib.BetterRepackRepositoryDefinitions;

namespace BDSM.Lib;
public static class Configuration
{
	private static ILogger logger = LogManager.CreateNullLogger();
	public static void InitializeLogger(ILogger parent) => logger = parent;
	internal const string USER_CONFIG_FILENAME = "UserConfiguration.yaml";
	public static readonly SimpleUserConfiguration.Modpacks DefaultModpacksSimpleHS2 = new()
	{
		Main = true,
		MEShaders = true,
		Exclusive = true,
		UncensorSelector = true,
		HS2Maps = true,
		Studio = false,
		StudioMaps = false,
		BleedingEdge = false,
		Userdata = true
	};
	public static readonly SimpleUserConfiguration.Modpacks DefaultModpacksSimpleAIS = DefaultModpacksSimpleHS2 with { HS2Maps = false };
	public static readonly RawUserConfiguration.Modpacks DefaultModpacksRawHS2 = new()
	{
		Main = DefaultModpacksSimpleHS2.Main,
		MEShaders = DefaultModpacksSimpleHS2.MEShaders,
		Exclusive = DefaultModpacksSimpleHS2.Exclusive,
		UncensorSelector = DefaultModpacksSimpleHS2.UncensorSelector,
		HS2Maps = DefaultModpacksSimpleHS2.HS2Maps,
		Studio = DefaultModpacksSimpleHS2.Studio,
		StudioMaps = DefaultModpacksSimpleHS2.StudioMaps,
		BleedingEdge = DefaultModpacksSimpleHS2.BleedingEdge,
		Userdata = DefaultModpacksSimpleHS2.Userdata
	};
	public static readonly RawUserConfiguration.Modpacks DefaultModpacksRawAIS = DefaultModpacksRawHS2 with { HS2Maps = false };
	public const bool DefaultPromptToContinue = true;
	public readonly record struct OldUserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required bool PromptToContinue { get; init; }
		public required string[] BaseSideloaderDirectories { get; init; }
	}
	public readonly record struct FullUserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required ImmutableHashSet<PathMapping> BasePathMappings { get; init; }
		public required bool PromptToContinue { get; init; }
	}
	public readonly record struct RawUserConfiguration
	{
		public readonly record struct Modpacks
		{
			public bool? Main { get; init; }
			public bool? MEShaders { get; init; }
			public bool? UncensorSelector { get; init; }
			public bool? Exclusive { get; init; }
			public bool? StudioMaps { get; init; }
			public bool? HS2Maps { get; init; }
			public bool? Studio { get; init; }
			public bool? BleedingEdge { get; init; }
			public bool? Userdata { get; init; }
		}
		public required string GamePath { get; init; }
		public Modpacks? OptionalModpacks { get; init; }
		public bool? PromptToContinue { get; init; }
	}
	public readonly record struct SimpleUserConfiguration
	{
		public readonly record struct Modpacks
		{
			public Modpacks ()
			{
				Main = true;
				MEShaders = true;
				UncensorSelector = true;
				Exclusive = true;
			}
			public bool Main { get; init; }
			public bool MEShaders { get; init; }
			public bool UncensorSelector { get; init; }
			public bool Exclusive { get; init; }
			public required bool StudioMaps { get; init; }
			public required bool HS2Maps { get; init; }
			public required bool Studio { get; init; }
			public required bool BleedingEdge { get; init; }
			public required bool Userdata { get; init; }
		}
		public required string GamePath { get; init; }
		public required Modpacks OptionalModpacks { get; init; }
		public required bool PromptToContinue { get; init; }
	}
	public readonly record struct RepoConnectionInfo
	{
		public required string Address { get; init; }
		public required string Username { get; init; }
		public string EffectivePassword => Password ?? Encoding.UTF8.GetString(Convert.FromBase64String(PasswordB64 ?? ""));
		public required string? Password { get; init; }
		public required string? PasswordB64 { get; init; }
		public required int Port { get; init; }
		public required string RootPath { get; init; }
		public required int MaxConnections { get; init; }
	}
	public readonly record struct ModpackDefinition
	{
		public required string Name { get; init; }
		public required string RemoteRelativePath { get; init; }
		public required string LocalRelativePath { get; init; }
		public required bool DeleteClientFiles { get; init; }
	}
	public static string ReadConfigAndDispose(string filename) { using StreamReader reader = File.OpenText(filename); return reader.ReadToEnd(); }
	public static async Task<string> ReadConfigAndDisposeAsync(string filename) { using StreamReader reader = File.OpenText(filename); return await reader.ReadToEndAsync(); }
	public static async Task<FullUserConfiguration> GetOldUserConfigurationAsync()
	{
		logger.Info("Creating configuration from the old format.");
		Deserializer yaml_deserializer = new();

		OldUserConfiguration old_config;
		try { old_config = yaml_deserializer.Deserialize<OldUserConfiguration>(await ReadConfigAndDisposeAsync(USER_CONFIG_FILENAME)); }
		catch (Exception ex)
		{
			string ucex_message = ex switch
			{
				TypeInitializationException when ex.InnerException is FileNotFoundException => "Your configuration file is missing. Please read the documentation and copy the example configuration to your own UserConfiguration.yaml.",
				TypeInitializationException => "Your configuration file is malformed. Please reference the example and read the documentation.",
				_ => "Unspecified error loading user configuration."
			};
			throw new UserConfigurationException(ucex_message, ex);
		}
		logger.Info("Reading old configuration format was successful. Converting to the new format.");
		return ConvertOldUserConfig(old_config);
	}
	public static FullUserConfiguration GetUserConfiguration(out string config_version, string? config_yaml = null)
	{
		Deserializer yaml_deserializer = new();
		RawUserConfiguration raw_config;
		try
		{
			raw_config = config_yaml is null
				? yaml_deserializer.Deserialize<RawUserConfiguration>(ReadConfigAndDispose(USER_CONFIG_FILENAME))
				: yaml_deserializer.Deserialize<RawUserConfiguration>(config_yaml);
		}
		catch (Exception ex)
		{
			throw ex switch
			{
				FileNotFoundException => new UserConfigurationException("Configuration file is missing.", ex),
				TypeInitializationException or YamlException => new UserConfigurationException("Configuration file is malformed.", ex),
				NullReferenceException => new UserConfigurationException("Configuration file is empty.", ex),
				_ => new UserConfigurationException("Unspecified error loading user configuration.", ex)
			};
		}

		SimpleUserConfiguration simple_config = ValidateRawUserConfiguration(raw_config, out config_version);
		return SimpleUserConfigurationToFull(simple_config);
	}
	private static SimpleUserConfiguration ValidateRawUserConfiguration(RawUserConfiguration nullable_config, out string config_version)
	{
		logger.Debug("Validating user configuration");
		if (nullable_config.GamePath is null)
			throw new UserConfigurationException("GamePath is missing from the configuration file.");
		bool is_hs2 = GamePathIsHS2(nullable_config.GamePath) ?? throw new UserConfigurationException($"{nullable_config.GamePath} is not a valid HS2 or AIS game directory.");
		config_version = nullable_config.OptionalModpacks?.Main is null ? "0.3" : "0.3.2";
		bool studio = nullable_config.OptionalModpacks?.Studio ?? DefaultModpacksSimpleHS2.Studio;
		logger.Debug("User configuration was verified.");
		return new()
		{
			GamePath = nullable_config.GamePath,
			OptionalModpacks = new()
			{
				Studio = studio,
				StudioMaps = nullable_config.OptionalModpacks?.StudioMaps ?? studio,
				HS2Maps = nullable_config.OptionalModpacks?.HS2Maps ?? is_hs2,
				BleedingEdge = nullable_config.OptionalModpacks?.BleedingEdge ?? DefaultModpacksSimpleHS2.BleedingEdge,
				Userdata = nullable_config.OptionalModpacks?.Userdata ?? DefaultModpacksSimpleHS2.Userdata
			},
			PromptToContinue = nullable_config.PromptToContinue ?? DefaultPromptToContinue
		};
	}
	public static void SerializeUserConfiguration(SimpleUserConfiguration userconfig)
	{
		string yaml_config = new Serializer().Serialize(userconfig);
		using StreamWriter config_file_writer = File.CreateText(USER_CONFIG_FILENAME);
		config_file_writer.Write(yaml_config);
	}
	public static bool? GamePathIsHS2(string path)
	{
		DirectoryInfo gamedir = new(path);
		if (!gamedir.Exists)
			return null;
		foreach (FileInfo fileinfo in gamedir.EnumerateFiles())
		{
			if (fileinfo.Name is "HoneySelect2.exe") return true;
			else if (fileinfo.Name is "AI-Shoujo.exe") return false;
		}
		return null;
	}
	public static SimpleUserConfiguration FullUserConfigurationToSimple(FullUserConfiguration full_config)
	{
		bool main = false;
		bool me_shaders = false;
		bool uncensor_selector = false;
		bool exclusive = false;
		bool maps = false;
		bool maps_hs2 = false;
		bool studio = false;
		bool bleedingedge = false;
		bool userdata = false;
		foreach (PathMapping pm in full_config.BasePathMappings)
		{
			switch (pm.FileName)
			{
				case MainModpackName:
					main = true; break;
				case MEShadersModpackName:
					me_shaders = true; break;
				case UncensorSelectorModpackName:
					uncensor_selector = true; break;
				case ExclusiveAISModpackName or ExclusiveHS2ModpackName:
					exclusive = true; break;
				case StudioMapsModpackName:
					maps = true; break;
				case HS2MapsModpackName:
					maps_hs2 = true; break;
				case BleedingEdgeModpackName:
					bleedingedge = true; break;
				case StudioModpackName:
					studio = true; break;
				case UserDataDirectoryName:
					userdata = true; break;
				default:
					throw new ArgumentException($"The user configuration provided has invalid data. {pm.FileName} is not a valid modpack directory.");
			}
		}
		SimpleUserConfiguration.Modpacks modpack_config = new()
		{
			Main = main,
			MEShaders = me_shaders,
			UncensorSelector = uncensor_selector,
			Exclusive = exclusive,
			StudioMaps = maps,
			HS2Maps = maps_hs2,
			BleedingEdge = bleedingedge,
			Studio = studio,
			Userdata = userdata
		};
		return new() { GamePath = full_config.GamePath, PromptToContinue = full_config.PromptToContinue, OptionalModpacks = modpack_config };
	}
	public static FullUserConfiguration SimpleUserConfigurationToFull(SimpleUserConfiguration simple_config)
	{
		bool is_hs2;
		SimpleUserConfiguration.Modpacks desired_modpacks = simple_config.OptionalModpacks;
		bool? _is_hs2 = GamePathIsHS2(simple_config.GamePath);
		is_hs2 = _is_hs2 is not null
			? (bool)_is_hs2
			: throw new UserConfigurationException($"{simple_config.GamePath} is not a valid HS2 or AIS game directory.");
		ImmutableHashSet<string> modpack_names = GetDesiredModpackNames(is_hs2, desired_modpacks);
		return new()
		{
			GamePath = simple_config.GamePath,
			ConnectionInfo = DefaultConnectionInfo,
			BasePathMappings = ModpackNamesToPathMappings(modpack_names, simple_config.GamePath, DefaultConnectionInfo.RootPath),
			PromptToContinue = simple_config.PromptToContinue
		};
	}
	public static FullUserConfiguration ConvertOldUserConfig(OldUserConfiguration old_config) => new()
	{
		GamePath = old_config.GamePath,
		ConnectionInfo = DefaultConnectionInfo,
		PromptToContinue = old_config.PromptToContinue,
		BasePathMappings = GetPathMappingsFromOldUserConfig(old_config)
	};
	public static ImmutableHashSet<PathMapping> GetPathMappingsFromOldUserConfig(OldUserConfiguration userconfig)
	{
		ImmutableHashSet<PathMapping>.Builder _pathmapping_builder = ImmutableHashSet.CreateBuilder<PathMapping>();
		foreach (string sideloaderdir in userconfig.BaseSideloaderDirectories)
		{
			string[] _sideloadersplit = sideloaderdir.Split(" | ");
			if (_sideloadersplit.Length != 3)
				throw new UserConfigurationException("Modpack definition string is malformed.");
			if (!bool.TryParse(_sideloadersplit[2], out bool _deletefiles))
				throw new UserConfigurationException("Modpack definition string is malformed.");
			PathMapping _pathmap = new()
			{
				RootPath = userconfig.ConnectionInfo.RootPath,
				RemoteRelativePath = _sideloadersplit[0],
				GamePath = userconfig.GamePath,
				LocalRelativePath = _sideloadersplit[1],
				FileSize = null,
				DeleteClientFiles = _deletefiles
			};
			if (!new DirectoryInfo(_pathmap.LocalFullPath).Exists)
				_ = Directory.CreateDirectory(_pathmap.LocalFullPath);
			_ = _pathmapping_builder.Add(_pathmap);
		}
		return _pathmapping_builder.ToImmutableHashSet();
	}
	public static PathMapping ModpackDefinitionToPathMapping(ModpackDefinition definition, string gamepath, string rootpath) => new()
	{
		RootPath = rootpath,
		RemoteRelativePath = definition.RemoteRelativePath,
		GamePath = gamepath,
		LocalRelativePath = definition.LocalRelativePath,
		FileSize = null,
		DeleteClientFiles = definition.DeleteClientFiles
	};
	public class ConfigurationException : Exception
	{
		public ConfigurationException() { }
		public ConfigurationException(string? message) : base(message) { }
		public ConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class UserConfigurationException : ConfigurationException
	{
		public UserConfigurationException() { }
		public UserConfigurationException(string? message) : base(message) { }
		public UserConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
}
