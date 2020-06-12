using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using Dapper;

using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

using PluralKit.Core;

using Serilog;

namespace PluralKit.Bot
{
    public class ProxyService
    {
        public static readonly TimeSpan MessageDeletionDelay = TimeSpan.FromMilliseconds(1000);

        private readonly LogChannelService _logChannel;
        private readonly DbConnectionFactory _db;
        private readonly IDataStore _data;
        private readonly ILogger _logger;
        private readonly WebhookExecutorService _webhookExecutor;
        private readonly ProxyMatcher _matcher;

        public ProxyService(LogChannelService logChannel, IDataStore data, ILogger logger,
                            WebhookExecutorService webhookExecutor, DbConnectionFactory db, ProxyMatcher matcher)
        {
            _logChannel = logChannel;
            _data = data;
            _webhookExecutor = webhookExecutor;
            _db = db;
            _matcher = matcher;
            _logger = logger.ForContext<ProxyService>();
        }

        public async Task<bool> HandleIncomingMessage(DiscordMessage message, MessageContext ctx, bool allowAutoproxy)
        {
            if (!ShouldProxy(message, ctx)) return false;

            // Fetch members and try to match to a specific member
            var members = (await _db.Execute(c => c.QueryProxyMembers(message.Author.Id, message.Channel.GuildId))).ToList();
            if (!_matcher.TryMatch(ctx, members, out var match, message.Content, message.Attachments.Count > 0,
                allowAutoproxy)) return false;

            // Permission check after proxy match so we don't get spammed when not actually proxying
            if (!await CheckBotPermissionsOrError(message.Channel)) return false;
            if (!CheckProxyNameBoundsOrError(match.Member.ProxyName(ctx))) return false;

            // Everything's in order, we can execute the proxy!
            await ExecuteProxy(message, ctx, match);
            return true;
        }

        private bool ShouldProxy(DiscordMessage msg, MessageContext ctx)
        {
            // Make sure author has a system
            if (ctx.SystemId == null) return false;
            
            // Make sure channel is a guild text channel and this is a normal message
            if (msg.Channel.Type != ChannelType.Text || msg.MessageType != MessageType.Default) return false;
            
            // Make sure author is a normal user
            if (msg.Author.IsSystem == true || msg.Author.IsBot || msg.WebhookMessage) return false;
            
            // Make sure proxying is enabled here
            if (!ctx.ProxyEnabled || ctx.InBlacklist) return false;
            
            // Make sure we have either an attachment or message content
            var isMessageBlank = msg.Content == null || msg.Content.Trim().Length == 0;
            if (isMessageBlank && msg.Attachments.Count == 0) return false;
            
            // All good!
            return true;
        }

        private async Task ExecuteProxy(DiscordMessage trigger, MessageContext ctx, ProxyMatch match)
        {
            // Send the webhook
            var id = await _webhookExecutor.ExecuteWebhook(trigger.Channel, match.Member.ProxyName(ctx),
                match.Member.ProxyAvatar(ctx),
                match.Content, trigger.Attachments);

            // Handle post-proxy actions
            await _data.AddMessage(trigger.Author.Id, trigger.Channel.GuildId, trigger.Channel.Id, id, trigger.Id,
                match.Member.Id);
            await _logChannel.LogMessage(ctx, match, trigger, id);

            // Wait a second or so before deleting the original message
            await Task.Delay(MessageDeletionDelay);
            try
            {
                await trigger.DeleteAsync();
            }
            catch (NotFoundException)
            {
                // If it's already deleted, we just log and swallow the exception
                _logger.Warning("Attempted to delete already deleted proxy trigger message {Message}", trigger.Id);
            }
        }
        
        private async Task<bool> CheckBotPermissionsOrError(DiscordChannel channel)
        {
            var permissions = channel.BotPermissions();

            // If we can't send messages at all, just bail immediately.
            // 2020-04-22: Manage Messages does *not* override a lack of Send Messages.
            if ((permissions & Permissions.SendMessages) == 0) return false;

            if ((permissions & Permissions.ManageWebhooks) == 0)
            {
                // todo: PKError-ify these
                await channel.SendMessageAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Webhooks* permission in this channel, and thus cannot proxy messages. Please contact a server administrator to remedy this.");
                return false;
            }

            if ((permissions & Permissions.ManageMessages) == 0)
            {
                await channel.SendMessageAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Messages* permission in this channel, and thus cannot delete the original trigger message. Please contact a server administrator to remedy this.");
                return false;
            }

            return true;
        }

        private bool CheckProxyNameBoundsOrError(string proxyName)
        {
            if (proxyName.Length < 2) throw Errors.ProxyNameTooShort(proxyName);
            if (proxyName.Length > Limits.MaxProxyNameLength) throw Errors.ProxyNameTooLong(proxyName);

            // TODO: this never returns false as it throws instead, should this happen?
            return true;
        }
    }
}