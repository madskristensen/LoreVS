using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Drives the reactive authentication flow shared by the clone/create/push/pull commands. There
    /// are deliberately no explicit sign-in/sign-out commands: like Git's credential manager, the
    /// user is prompted to authenticate only when a server operation fails for an auth reason, and
    /// the operation is retried once after a successful sign-in. The native SDK persists the session,
    /// so later operations against the same server authenticate without prompting again.
    /// </summary>
    internal static class LoreAuthFlow
    {
        /// <summary>
        /// Runs <paramref name="operation"/> on a background thread and, if it fails because the
        /// server requires authentication, offers to sign in and then retries the operation once.
        /// Returns the final result (the original failure when the user declines or sign-in fails).
        /// </summary>
        /// <param name="client">The Lore client used to run the operation and to sign in.</param>
        /// <param name="workingDirectory">
        /// Repository path used to resolve the server when <paramref name="remoteUrl"/> is empty
        /// (push/pull); may be empty for clone.
        /// </param>
        /// <param name="remoteUrl">Explicit server/repository URL, or empty to resolve from the repo.</param>
        /// <param name="operation">The server operation to run (and retry after sign-in).</param>
        public static async Task<LoreCommandResult> ExecuteAsync(
            ILoreClient client,
            string workingDirectory,
            string remoteUrl,
            Func<LoreCommandResult> operation)
        {
            LoreCommandResult result = await Task.Run(operation);
            if (result.Success || !result.RequiresAuthentication)
            {
                return result;
            }

            if (!await SignInAsync(client, workingDirectory, remoteUrl))
            {
                return result;
            }

            return await Task.Run(operation);
        }

        /// <summary>
        /// Prompts the user to authenticate and, on confirmation, runs the interactive sign-in flow.
        /// Returns true when sign-in succeeded. Safe to call from any thread; switches to the UI
        /// thread for the prompts.
        /// </summary>
        public static async Task<bool> SignInAsync(ILoreClient client, string workingDirectory, string remoteUrl)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string server = string.IsNullOrWhiteSpace(remoteUrl) ? "the Lore server" : remoteUrl;
            bool confirmed = await VS.MessageBox.ShowConfirmAsync(
                "Lore",
                $"You need to sign in to {server} to continue.\n\n" +
                "Your default browser will open to complete sign-in. Continue?");
            if (!confirmed)
            {
                return false;
            }

            await VS.StatusBar.ShowMessageAsync("Waiting for Lore sign-in in your browser...");
            LoreAuthResult auth = await Task.Run(() => client.Login(workingDirectory ?? string.Empty, remoteUrl ?? string.Empty));
            await LoreLog.WriteCommandAsync(
                "auth login",
                auth.Success
                    ? $"Signed in{(string.IsNullOrEmpty(auth.UserName) ? string.Empty : " as " + auth.UserName)}."
                    : "Sign-in failed: " + auth.ErrorMessage);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!auth.Success)
            {
                await VS.StatusBar.ShowMessageAsync("Lore sign-in failed.");
                string detail = string.IsNullOrWhiteSpace(auth.ErrorMessage)
                    ? "Sign-in did not complete."
                    : auth.ErrorMessage;
                await VS.MessageBox.ShowErrorAsync("Lore", "Lore sign-in failed:\n\n" + detail);
                return false;
            }

            string who = string.IsNullOrEmpty(auth.UserName) ? string.Empty : $" as {auth.UserName}";
            await VS.StatusBar.ShowMessageAsync($"Signed in to Lore{who}.");
            return true;
        }
    }
}
