using System.Text;

using YamlDotNet.Serialization;

namespace BDSM;
public record Configuration
{
	public const string UserConfigurationFilename = "UserConfiguration.yaml";
	public const string SkipScanConfigurationFilename = "SkipScan.yaml";
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
	public readonly record struct UserConfiguration
	{
		public required string GamePath { get; init; }
		public required RepoConnectionInfo ConnectionInfo { get; init; }
		public required bool PromptToContinue { get; init; }
		public required string[] BaseSideloaderDirectories { get; init; }
	}
	public static string ReadConfigAndDispose(string filename) => ReadConfigAndDisposeAsync(filename).Result;
	public static async Task<string> ReadConfigAndDisposeAsync(string filename) { using StreamReader reader = File.OpenText(filename); return await reader.ReadToEndAsync(); }
	public static async Task<UserConfiguration> GetUserConfigurationAsync()
	{
		UserConfiguration _config;
		try { _config = new Deserializer().Deserialize<UserConfiguration>(await ReadConfigAndDisposeAsync(UserConfigurationFilename)); }
		catch (Exception ex)
		{
			string ucex_message = ex switch
			{
				TypeInitializationException when ex.InnerException is FileNotFoundException => "Your configuration file is missing. Please read the documentation and copy the example configuration to your own UserConfiguration.yaml.",
				TypeInitializationException => "Your configuration file is malformed. Please reference the example and read the documentation.",
				_ => "Unspecified error loading configuration."
			};
		throw new UserConfigurationException(ucex_message, ex);
		}
		return _config;
	}
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
