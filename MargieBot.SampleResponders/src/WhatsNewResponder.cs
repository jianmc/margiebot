﻿using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MargieBot.SampleResponders
{
    public class WhatsNewResponder : IResponder
    {
        public bool CanRespond(ResponseContext context)
        {
            return (context.Message.MentionsBot || context.Message.ChatHub.Type == SlackChatHubType.DM) && Regex.IsMatch(context.Message.Text, @"\b(what's new)|(whats new)\b", RegexOptions.IgnoreCase);
        }

        public async Task<BotMessage> GetResponse(ResponseContext context)
        {
            // TODO: better way to do this?
            Version version = typeof(WhatsNewResponder).GetTypeInfo().Assembly.GetName().Version;

            string message =
                @"I'm " + context.BotUserName + " v." +
                version.Major.ToString() + "." +
                version.Minor.ToString() + "." +
                version.Build.ToString() + "! Here's what all's been goin' on with me lately.```" +
                "- Those nerdy bots down at my local game store have suckered me into learnin' how to play Dungeons and Draggins! Ask me about my character!\n" +
                "- My internet legacy is growin', y'all! My wiki at https://github.com/jammerware/margiebot/wiki is real polished now, and you can add me to your next bot project from NuGet! Just Install-Package MargieBot. \n" +
                "```";

            return new BotMessage() { Text = message };
        }
    }
}