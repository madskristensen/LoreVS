namespace LoreVS.SourceControl
{
    /// <summary>
    /// Result of running a Lore write/onboarding operation. Carries enough
    /// information for a command to report success or surface the error to the user.
    /// </summary>
    public sealed class LoreCommandResult
    {
        public LoreCommandResult(bool success, int exitCode, string output, string error)
        {
            Success = success;
            ExitCode = exitCode;
            Output = output ?? string.Empty;
            Error = error ?? string.Empty;
        }

        /// <summary>True when the command ran and exited with code 0.</summary>
        public bool Success { get; }

        /// <summary>Process exit code (-1 when the CLI could not be launched or timed out).</summary>
        public int ExitCode { get; }

        /// <summary>Captured standard output.</summary>
        public string Output { get; }

        /// <summary>Captured standard error (or a human message when launch failed).</summary>
        public string Error { get; }

        /// <summary>Combined stdout/stderr, trimmed, for display in the output window.</summary>
        public string CombinedText
        {
            get
            {
                string text = Output;
                if (!string.IsNullOrWhiteSpace(Error))
                {
                    text = string.IsNullOrWhiteSpace(text) ? Error : text + System.Environment.NewLine + Error;
                }

                return (text ?? string.Empty).Trim();
            }
        }

        public static LoreCommandResult Failed(string error) =>
            new LoreCommandResult(false, -1, string.Empty, error);
    }
}
