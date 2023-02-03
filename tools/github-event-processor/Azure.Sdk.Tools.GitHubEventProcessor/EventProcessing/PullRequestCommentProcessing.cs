using System;
using System.Collections.Generic;
using System.Text;
using Azure.Sdk.Tools.GitHubEventProcessor.GitHubPayload;
using Azure.Sdk.Tools.GitHubEventProcessor.Utils;
using Octokit.Internal;
using Octokit;
using System.Threading.Tasks;
using Azure.Sdk.Tools.GitHubEventProcessor.Constants;

namespace Azure.Sdk.Tools.GitHubEventProcessor.EventProcessing
{
    public class PullRequestCommentProcessing
    {
        public static async Task ProcessPullRequestCommentEvent(GitHubEventClient gitHubEventClient, IssueCommentPayload prCommentPayload)
        {
            // If the Issue Comment isn't a PullRequest Comment
            if (prCommentPayload.Issue.PullRequest == null) 
            {
                return;
            }

            await ResetPullRequestActivity(gitHubEventClient, prCommentPayload);
            await ReopenPullRequest(gitHubEventClient, prCommentPayload);

            // After all of the rules have been processed, call to process pending updates
            int numUpdates = await gitHubEventClient.ProcessPendingUpdates(prCommentPayload.Repository.Id, prCommentPayload.Issue.Number);
        }

        /// <summary>
        /// Reset Pull Request Activity https://gist.github.com/jsquire/cfff24f50da0d5906829c5b3de661a84#reset-pull-request-activity
        /// Normally there's supposed to be a common function but the PullRequest object on the IssueCommentPayload isn't complete.
        /// All of the things like labels and whatnot need to come from the Issue.
        /// Trigger: issue_comment created on a pull request
        /// Conditions for pull request comment
        ///     Pull request has "no-recent-activity" label
        ///     User modifying the pull request is not a bot
        ///     Commenter is the pull request author OR has admin or write permissions
        ///     Comment is not a /check-enforcer or /azp command
        ///     Comment is not from an auto-close event. Note: This could happen if the
        ///     the cron task wasn't executed with secrets.GITHUB_TOKEN (aka someone ran
        ///     the task from their own personal token.)
        /// </summary>
        /// <param name="gitHubClient">Authenticated GitHubClient</param>
        /// <param name="prCommentPayload">Pull Request Comment event payload</param>
        /// <param name="issueUpdate">The issue update object</param>
        /// <returns></returns>
        public static async Task ResetPullRequestActivity(GitHubEventClient gitHubEventClient,
                                                          IssueCommentPayload prCommentPayload)
        {
            if (gitHubEventClient.RulesConfiguration.RuleEnabled(RulesConstants.ResetPullRequestActivity))
            {
                if (prCommentPayload.Sender.Type != AccountType.Bot &&
                    LabelUtils.HasLabel(prCommentPayload.Issue.Labels, LabelConstants.NoRecentActivity))
                {
                    if (prCommentPayload.Action == ActionConstants.Created &&
                        prCommentPayload.Issue.State == ItemState.Open &&
                        !CommentUtils.CommentContainsText(prCommentPayload.Comment.Body, CommentConstants.CheckEnforcer) &&
                        !CommentUtils.CommentContainsText(prCommentPayload.Comment.Body, CommentConstants.Azp) &&
                        !CommentUtils.CommentContainsText(prCommentPayload.Comment.Body, CommentConstants.ScheduledCloseFragment))
                    {
                        bool removeLabel = false;
                        // If the commenter is the pull request author skip then the label can be
                        // removed without a permissions check
                        if (prCommentPayload.Issue.User.Login == prCommentPayload.Sender.Login)
                        {
                            removeLabel = true;
                        }
                        else
                        {
                            // If the user who made the comment has admin or write permissions then
                            // the label can be removed
                            bool hasWriteOrAdminPermissions = await gitHubEventClient.DoesUserHaveAdminOrWritePermission(prCommentPayload.Repository.Id, prCommentPayload.Sender.Login);
                            if (hasWriteOrAdminPermissions)
                            {
                                removeLabel = true;
                            }
                        }

                        if (removeLabel)
                        {
                            var issueUpdate = gitHubEventClient.GetIssueUpdate(prCommentPayload.Issue);
                            issueUpdate.RemoveLabel(LabelConstants.NoRecentActivity);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Reopen Pull Request https://gist.github.com/jsquire/cfff24f50da0d5906829c5b3de661a84#reopen-pull-request
        /// Trigger: pull comment created
        /// Conditions:
        ///     Pull request is closed
        ///     Pull request has "no-recent-activity" label
        ///     Comment text contains the string "/reopen"
        ///     Commenter is the pull request author OR has write or admin permission
        /// Resulting Action:
        ///     if the Commenter is the pull request author OR has write or admin permission
        ///         Remove "no-recent-activity" label
        ///         Reopen pull request
        ///     else
        ///         Create a comment: "Sorry, @commenter, only the original author or someone with write collaborator permissions can reopen this pull request."
        /// </summary>
        /// <param name="gitHubEventClient"></param>
        /// <param name="prCommentPayload"></param>
        /// <param name="issueUpdate"></param>
        public static async Task ReopenPullRequest(GitHubEventClient gitHubEventClient, IssueCommentPayload prCommentPayload)
        {
            if (gitHubEventClient.RulesConfiguration.RuleEnabled(RulesConstants.ReopenPullRequest))
            {
                if (prCommentPayload.Action == ActionConstants.Created)
                {
                    if (prCommentPayload.Issue.State == ItemState.Closed &&
                        LabelUtils.HasLabel(prCommentPayload.Issue.Labels, LabelConstants.NoRecentActivity) &&
                        CommentUtils.CommentContainsText(prCommentPayload.Comment.Body, CommentConstants.Reopen))
                    {
                        bool reOpen = false;
                        if (prCommentPayload.Sender.Login == prCommentPayload.Issue.User.Login)
                        {
                            reOpen = true;
                        }
                        else
                        {
                            bool hasWriteOrAdminPermissions = await gitHubEventClient.DoesUserHaveAdminOrWritePermission(prCommentPayload.Repository.Id, prCommentPayload.Sender.Login);
                            if (hasWriteOrAdminPermissions)
                            {
                                reOpen = true;
                            }
                        }
                        if (reOpen)
                        {
                            var issueUpdate = gitHubEventClient.GetIssueUpdate(prCommentPayload.Issue);
                            issueUpdate.State = ItemState.Open;
                            issueUpdate.RemoveLabel(LabelConstants.NoRecentActivity);
                        }
                        else
                        {
                            string prComment = $"Sorry, @{prCommentPayload.Sender.Login}, only the original author or someone with write collaborator permissions can reopen this pull request.";
                            gitHubEventClient.CreateComment(prCommentPayload.Repository.Id, prCommentPayload.Issue.Number, prComment);
                        }
                    }
                }
            }
        }
    }
}
