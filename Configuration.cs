using System.Collections.Immutable;
using System.Text;

using YamlDotNet.Serialization;

namespace BDSM;
public record Configuration
{
	internal const string USER_CONFIG_FILENAME = "UserConfiguration.yaml";
	internal const bool USE_OLD_CONFIG = true;

	public readonly record struct OldUserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required bool PromptToContinue { get; init; }
		public required string[] BaseSideloaderDirectories { get; init; }
	}
	public readonly record struct UserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required ImmutableHashSet<PathMapping> BasePathMappings { get; init; }
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
	public static async Task<UserConfiguration> GetUserConfigurationAsync()
	{
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

		return new()
		{
			GamePath = old_config.GamePath,
			ConnectionInfo = BetterRepackRepositoryDefinitions.DefaultConnectionInfo,
			PromptToContinue = old_config.PromptToContinue,
			BasePathMappings = GetPathMappingsFromOldUserConfig(old_config)
		};
	}
	public static ImmutableHashSet<PathMapping> GetPathMappingsFromOldUserConfig(OldUserConfiguration userconfig)
	{
		ImmutableHashSet<PathMapping>.Builder _pathmapping_builder = ImmutableHashSet.CreateBuilder<PathMapping>();
		foreach (string sideloaderdir in userconfig.BaseSideloaderDirectories)
		{
			string[] _sideloadersplit = sideloaderdir.Split(" | ");
			bool _deletefiles = bool.Parse(_sideloadersplit[2]);
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
