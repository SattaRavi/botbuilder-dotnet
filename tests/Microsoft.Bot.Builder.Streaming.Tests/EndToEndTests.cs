﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.DirectLine;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Streaming;
using Microsoft.Bot.Streaming.Transport.NamedPipes;
using Microsoft.Net.Http.Headers;
using Newtonsoft;
using Newtonsoft.Json;
using Xunit;

namespace Microsoft.Bot.Builder.Streaming.Tests
{
    public class EndToEndTests
    {
        [Fact]
        public async Task EndToEnd_PostActivityToBot()
        {
            // Arrange
            object syncLock = new object();
            MockBot mockBot = null;
            var client = new NamedPipeClient("testPipes");
            var conversation = new Conversation(conversationId: "conversation1");
            var processActivity = ProcessActivityWithAttachments(mockBot, conversation);
            mockBot = new MockBot(processActivity);
            var requestWithOutActivity = GetStreamingRequestWithoutAttachments(conversation.ConversationId);

            // Act
            await client.ConnectAsync();
            var response = await client.SendAsync(requestWithOutActivity);

            // Assert
            Assert.Equal(200, response.StatusCode);
        }

        [Fact]
        public async Task EndToEnd_PostActivityWithAttachmentToBot()
        {
            // Arrange
            object syncLock = new object();
            MockBot mockBot = null;
            var client = new NamedPipeClient("testPipes");
            var conversation = new Conversation(conversationId: "conversation1");
            var processActivity = ProcessActivityWithAttachments(mockBot, conversation);
            mockBot = new MockBot(processActivity);
            var requestWithAttachments = GetStreamingRequestWithAttachment(conversation.ConversationId);

            // Act
            await client.ConnectAsync();
            var response = await client.SendAsync(requestWithAttachments);

            // Assert
            Assert.Equal(200, response.StatusCode);
        }

        private static StreamingRequest GetStreamingRequestWithoutAttachments(string conversationId)
        {
            var conId = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString() : conversationId;

            var request = new StreamingRequest()
            {
                Verb = "POST",
                Path = $"/v3/directline/conversations/{conId}/activities",
            };

            var activity = new Schema.Activity()
            {
                Type = "message",
                Text = "hello",
                ServiceUrl = "urn:test:namedpipe:testPipes",
                From = new Schema.ChannelAccount()
                {
                    Id = "123",
                    Name = "Fred",
                },
                Conversation = new Schema.ConversationAccount(null, null, conId, null, null, null, null),
            };

            request.SetBody(activity);

            return request;
        }

        private static StreamingRequest GetStreamingRequestWithAttachment(string conversationId)
        {
            var conId = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString() : conversationId;
            var attachmentData = "blah blah i am a stream!";
            var streamContent = new MemoryStream(Encoding.UTF8.GetBytes(attachmentData));
            var attachmentStream = new AttachmentStream("botframework-stream", streamContent);

            var request = new StreamingRequest()
            {
                Verb = "POST",
                Path = $"/v3/directline/conversations/{conId}/activities",
            };
            var activity = new Schema.Activity()
            {
                Type = "message",
                Text = "hello",
                ServiceUrl = "urn:test:namedpipe:testPipes",
                From = new Schema.ChannelAccount()
                {
                    Id = "123",
                    Name = "Fred",
                },
                Conversation = new Schema.ConversationAccount(null, null, conId, null, null, null, null),
            };

            request.SetBody(activity);

            var contentStream = new StreamContent(attachmentStream.ContentStream);
            contentStream.Headers.TryAddWithoutValidation(HeaderNames.ContentType, attachmentStream.ContentType);
            request.AddStream(contentStream);

            return request;
        }

        private static Func<Schema.Activity, Task<InvokeResponse>> ProcessActivityWithAttachments(MockBot mockBot, Conversation conversation)
        {
            var attachmentStreamData = new List<string>();

            Func<Schema.Activity, Task<InvokeResponse>> processActivity = async (activity) =>
            {
                if (activity.Attachments != null)
                {
                    foreach (Schema.Attachment attachment in activity.Attachments)
                    {
                        if (attachment.ContentType.Contains("botframework-stream"))
                        {
                            var stream = attachment.Content as Stream;
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                attachmentStreamData.Add(reader.ReadToEnd());
                            }

                            var testActivity = new Schema.Activity()
                            {
                                Type = "message",
                                Text = "received from bot",
                                From = new Schema.ChannelAccount()
                                {
                                    Id = "bot",
                                    Name = "bot",
                                },
                                Conversation = new Schema.ConversationAccount(null, conversation.ConversationId, null),
                            };
                            var attachmentData1 = "blah blah i am a stream!";
                            var streamContent1 = new MemoryStream(Encoding.UTF8.GetBytes(attachmentData1));
                            var attachmentData2 = "blah blah i am also a stream!";
                            var streamContent2 = new MemoryStream(Encoding.UTF8.GetBytes(attachmentData2));
                            await mockBot.SendActivityAsync(testActivity, new List<AttachmentStream>()
                            {
                                new AttachmentStream("bot-stream1", streamContent1),
                            });
                            await mockBot.SendActivityAsync(testActivity, new List<AttachmentStream>()
                            {
                                new AttachmentStream("bot-stream2", streamContent2),
                            });
                        }
                    }
                }

                return null;
            };
            return processActivity;
        }

        private class MockBot : IBot
        {
            private readonly DirectLineAdapter _adapter;
            private readonly Func<Schema.Activity, Task<InvokeResponse>> _processActivityAsync;

            public MockBot(Func<Schema.Activity, Task<InvokeResponse>> processActivityAsync)
            {
                _processActivityAsync = processActivityAsync;
                _adapter = new DirectLineAdapter(null, this, null);
                _adapter.AddNamedPipeConnection("testPipes", this);
            }

            public List<Schema.Activity> ReceivedActivities { get; private set; } = new List<Schema.Activity>();

            public List<Schema.Activity> SentActivities { get; private set; } = new List<Schema.Activity>();

            public async Task<Schema.ResourceResponse> SendActivityAsync(Schema.Activity activity, List<AttachmentStream> attachmentStreams = null)
            {
                SentActivities.Add(activity);

                var requestPath = $"/v3/conversations/{activity.Conversation?.Id}/activities/{activity.Id}";
                var request = StreamingRequest.CreatePost(requestPath);
                request.SetBody(activity);
                attachmentStreams?.ForEach(a =>
                {
                    var streamContent = new StreamContent(a.ContentStream);
                    streamContent.Headers.TryAddWithoutValidation(HeaderNames.ContentType, a.ContentType);
                    request.AddStream(streamContent);
                });

                var serverResponse = await _adapter.ProcessActivityForStreamingChannelAsync(activity, CancellationToken.None).ConfigureAwait(false);

                if (serverResponse.Status == (int)HttpStatusCode.OK)
                {
                   return JsonConvert.DeserializeObject<Schema.ResourceResponse>(serverResponse.Body.ToString());
                }

                throw new Exception("SendActivityAsync failed");
        }

            public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
            {
                turnContext.SendActivityAsync(MessageFactory.Text($"Echo: {turnContext.Activity.Text}"), cancellationToken);

                return;

                // await SendActivityAsync(turnContext.Activity);
            }

            private Task<InvokeResponse> ProcessActivityAsync(Schema.Activity activity)
        {
            ReceivedActivities.Add(activity);

            return _processActivityAsync(activity);
        }
        }

        private class AttachmentStream
        {
            public AttachmentStream(string contentType, Stream stream)
            {
                ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
                ContentStream = stream ?? throw new ArgumentNullException(nameof(stream));
            }

            public string ContentType { get; }

            public Stream ContentStream { get; }
        }
    }
}
