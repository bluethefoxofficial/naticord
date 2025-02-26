﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Authentication;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using WebSocketSharp;

namespace Naticord
{
    public class WebSocketClientDM
    {
        private DM parentDMForm;
        private WebSocket webSocket;
        private string accessToken;
        private const SslProtocols Tls12 = (SslProtocols)0x00000C00;
        private bool tryingRandomStuffAtThisPoint = false;

        public WebSocketClientDM(string accessToken, DM parentDMForm)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            this.accessToken = accessToken;
            this.parentDMForm = parentDMForm;
            InitializeWebSocket();
        }

        private void InitializeWebSocket()
        {
            webSocket = new WebSocket($"wss://gateway.discord.gg/?v=9&encoding=json");
            webSocket.SslConfiguration.EnabledSslProtocols = Tls12;
            webSocket.OnMessage += async (sender, e) => await HandleWebSocketMessage(e.Data);
            webSocket.OnError += (sender, e) => HandleWebSocketError(e.Message);
            webSocket.OnClose += (sender, e) => HandleWebSocketClose();
            webSocket.Connect();
            SendIdentifyPayload();
        }

        private void SendIdentifyPayload()
        {
            if (webSocket.ReadyState == WebSocketState.Open)
            {
                var identifyPayload = new
                {
                    op = 2,
                    d = new
                    {
                        token = accessToken,
                        properties = new
                        {
                            os = "windows",
                            browser = "chrome",
                            device = "pc"
                        }
                    }
                };

                try
                {
                    webSocket.Send(Newtonsoft.Json.JsonConvert.SerializeObject(identifyPayload));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending identify payload: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("WebSocket connection is not open. Unable to send identify payload.");
            }
        }

        private async Task HandleWebSocketMessage(string data)
        {
            Console.WriteLine($"WebSocket Received: {data}");

            var json = JObject.Parse(data);
            int opCode = (int)json["op"];

            switch (opCode)
            {
                case 0:
                    string eventType = (string)json["t"];
                    switch (eventType)
                    {
                        case "TYPING_START":
                            HandleTypingStartEvent(json["d"]);
                            break;
                        case "MESSAGE_CREATE":
                            await HandleMessageCreateEventAsync(json["d"]);
                            HandleTypingStopEvent(json["d"]);
                            break;
                        case "PRESENCE_UPDATE":
                            //HandlePresenceUpdateEvent(json["d"]);
                            break;
                    }
                    break;
                default:
                    // handle other op codes when needed
                    break;
            }
        }

        private void HandleTypingStartEvent(JToken jToken)
        {
            string channelId = (string)jToken["channel_id"];
            if (long.TryParse(channelId, out long parsedChannelId) && parsedChannelId == parentDMForm.ChatID)
            {
                string userId = (string)jToken["user_id"];
                string username = GetUsernameById(userId);
                string message = $"{username} is typing...";

                parentDMForm.Invoke((MethodInvoker)(() =>
                {
                    parentDMForm.typingStatus.Text = message;
                }));
            }
        }

        private void HandleTypingStopEvent(JToken jToken)
        {
            string channelId = (string)jToken["channel_id"];
            if (long.TryParse(channelId, out long parsedChannelId) && parsedChannelId == parentDMForm.ChatID)
            {
                parentDMForm.Invoke((MethodInvoker)(() =>
                {
                    parentDMForm.typingStatus.Text = string.Empty;
                }));
            }
        }

        public string GetUsernameById(string userId)
        {
            try
            {
                dynamic user = parentDMForm.GetApiResponse($"users/{userId}");
                return user.username;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get username for user ID {userId}: {ex.Message}");
                return "Unknown";
            }
        }

        public class Attachment
        {
            public string URL { get; set; }
            public string Type { get; set; }
        }

        public class Embed
        {
            public string Type { get; set; }
            public string Author { get; set; }
            public string AuthorURL { get; set; }
            public string Title { get; set; }
            public string TitleURL { get; set; }
            public string Description { get; set; }
        }

        private async Task HandleMessageCreateEventAsync(JToken data)
        {
            dynamic eventData = data;
            dynamic attachmentData = eventData["attachments"];
            dynamic embedData = eventData["embeds"];
            string channelId = eventData["channel_id"];
            string author = eventData["author"]["global_name"];
            if (author == null) author = eventData["author"]["username"];
            string content = eventData["content"];
            var attachmentsFormed = new List<Attachment>();
            var embedsFormed = new List<Embed>();

            if (attachmentData != null)
            {
                foreach (var attachment in attachmentData)
                {
                    attachmentsFormed.Add(new Attachment { URL = attachment.url, Type = attachment.content_type });
                }
            }

            if (embedData != null)
            {
                foreach (var embed in embedData)
                {
                    embedsFormed.Add(new Embed { Type = embed?.type ?? "", Author = embed?.author?.name ?? "", AuthorURL = embed?.author?.url ?? "", Title = embed?.title ?? "", TitleURL = embed?.url ?? "", Description = embed?.description ?? "" });
                }
            }

            if (channelId == parentDMForm.ChatID.ToString())
            {
                switch ((int)eventData["type"].Value)
                {
                    case 7:
                        // Join message
                        parentDMForm.AddMessage(author, "*Say hi!*", "slid in the server", attachmentsFormed.ToArray(), embedsFormed.ToArray(), true, true);
                        break;

                    case 19:
                        // Reply
                        bool found = false;
                        var messages = await parentDMForm.GetApiResponse($"channels/{parentDMForm.ChatID}/messages");
                        foreach (var message in messages)
                        {
                            if (message.id == eventData["message_reference"]["message_id"])
                            {
                                string replyAuthor = message.author.global_name;
                                if (replyAuthor == null) replyAuthor = message.author.username;
                                parentDMForm.AddMessage(author, content, "replied", attachmentsFormed.ToArray(), embedsFormed.ToArray(), true, true, replyAuthor, message.content.Value);
                                found = true;
                                break;
                            }
                        }
                        if (!found) parentDMForm.AddMessage(author, content, "replied", attachmentsFormed.ToArray(), embedsFormed.ToArray(), true, true, " ", "Unable to load message");
                        break;

                    default:
                        // Normal text or unimplemented
                        parentDMForm.AddMessage(author, content, "said", attachmentsFormed.ToArray(), embedsFormed.ToArray(), true, true);
                        break;
                }
                parentDMForm.Invoke((MethodInvoker)(() => parentDMForm.ScrollToBottom()));
            }
        }

        /*private void HandlePresenceUpdateEvent(JToken data)
        {
            dynamic eventData = data;
            string userId = eventData["user"]["id"];
            string status = eventData["status"];

            string username = GetUsernameById(userId);

            parentDMForm.Invoke((MethodInvoker)(() =>
            {
                // nothing for now
            }));
        }*/

        private void HandleWebSocketError(string errorMessage)
        {
            parentDMForm.Invoke((MethodInvoker)(() =>
            {
                Console.WriteLine($"WebSocket Error: {errorMessage}");
                InitializeWebSocket();
            }));
        }

        private void HandleWebSocketClose()
        {
            if (!tryingRandomStuffAtThisPoint)
            {
                try
                {
                    parentDMForm.Invoke((MethodInvoker)(() =>
                    {
                        Console.WriteLine("WebSocket connection closed.");
                        InitializeWebSocket();
                    }));
                }
                catch { }
            }
        }

        public void CloseWebSocket()
        {
            tryingRandomStuffAtThisPoint = true;
            webSocket.Close();
            GC.Collect();
        }
    }
}
