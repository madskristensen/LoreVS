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

        /// <summary>Lore SDK status code (-1 for a worker, transport, or timeout failure).</summary>
        public int ExitCode { get; }

        /// <summary>Captured standard output.</summary>
        public string Output { get; }

        /// <summary>Captured standard error (or a human message when launch failed).</summary>
        public string Error { get; }

        /// <summary>
        /// True when the operation failed because the Lore server requires authentication (or the
        /// stored session is missing/expired). Drives the reactive sign-in prompt: the command offers
        /// to authenticate and then retries. Defaults to false; the worker sets it when it classifies
        /// a failure as auth-related.
        /// </summary>
        public bool RequiresAuthentication { get; set; }

        /// <summary>A non-fatal warning, such as sync falling back to local history.</summary>
        public string Warning { get; set; } = string.Empty;

        /// <summary>Combined output, error, and warning, trimmed, for the output window.</summary>
        public string CombinedText
        {
            get
            {
                string text = Output;
                if (!string.IsNullOrWhiteSpace(Error))
                {
                    text = string.IsNullOrWhiteSpace(text) ? Error : text + System.Environment.NewLine + Error;
                }

                if (!string.IsNullOrWhiteSpace(Warning))
                {
                    text = string.IsNullOrWhiteSpace(text) ? Warning : text + System.Environment.NewLine + Warning;
                }

                return (text ?? string.Empty).Trim();
            }
        }

        public static LoreCommandResult Failed(string error) =>
            new LoreCommandResult(false, -1, string.Empty, error);
    }
}
