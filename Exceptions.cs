namespace BDSM;
public record Exceptions
{
	public class BDSMInternalFaultException : Exception
	{
		const string BugReportSuffix = " Please file a bug report and provide this information. https://github.com/RobotsOnDrugs/BDSM/issues";
		public BDSMInternalFaultException() { }
		public BDSMInternalFaultException(string? message) : base(message + BugReportSuffix) { }
		public BDSMInternalFaultException(string? message, bool include_bug_report_link) : base(message + (include_bug_report_link ? "" : BugReportSuffix)) { }
		public BDSMInternalFaultException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class ConfigurationException : Exception
	{
		public ConfigurationException() { }
		public ConfigurationException(string? message) : base(message) { }
		public ConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class UserConfigurationException : ConfigurationException
	{
		public UserConfigurationException() {  }
		public UserConfigurationException(string? message) : base(message) { }
		public UserConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPOperationException : Exception
	{
		public FTPOperationException() { }
		public FTPOperationException(string? message) : base(message) { }
		public FTPOperationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
}
