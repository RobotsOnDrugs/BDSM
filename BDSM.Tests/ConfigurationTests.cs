using static BDSM.Lib.Configuration;
using static BDSM.Tests.ConfigurationTestData;

namespace BDSM.Tests;

public class ConfigurationTests
{
	[Fact]
	public void ConvertOldUserConfigDefault()
	{
		FullUserConfiguration expected = DefaultFullUserConfigurationHS2;
		FullUserConfiguration actual = ConvertOldUserConfig(OldUserConfigurationNormal);
		Assert.Equal(expected.GamePath, actual.GamePath);
		Assert.Equal(expected.ConnectionInfo, actual.ConnectionInfo);
		Assert.Equal(expected.BasePathMappings, actual.BasePathMappings);
		Assert.Equal(expected.PromptToContinue, actual.PromptToContinue);
	}
}