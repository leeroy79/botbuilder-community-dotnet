using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Builder.Community.Adapters.Alexa.Directives;
using Bot.Builder.Community.Adapters.Alexa.Integration;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Newtonsoft.Json;

namespace Bot.Builder.Community.Adapters.Alexa
{
    public class AlexaAdapter : BotAdapter
    {
        private Dictionary<string, List<Activity>> Responses { get; set; }

        private static ConcurrentDictionary<string, string> ConversationIdMap { get; set; }

        private static ConcurrentDictionary<string, string> UserIdMap { get; set; }

        private AlexaOptions Options { get; set; }

        public AlexaAdapter()
        {
            if (ConversationIdMap == null)
            {
                ConversationIdMap = new ConcurrentDictionary<string, string>();
            }

            if (UserIdMap == null)
            {
                UserIdMap = new ConcurrentDictionary<string, string>();
            }
        }

        public async Task<AlexaResponseBody> ProcessActivity(AlexaRequestBody alexaRequest, AlexaOptions alexaOptions, BotCallbackHandler callback)
        {
            TurnContext context = null;

            Trace.TraceInformation("Incoming Alexa request:");
            Trace.TraceInformation(JsonConvert.SerializeObject(alexaRequest));

            try
            {
                Options = alexaOptions;

                var activity = RequestToActivity(alexaRequest);
                BotAssert.ActivityNotNull(activity);

                Trace.TraceInformation("Transformed bot framework activity:");
                Trace.TraceInformation(JsonConvert.SerializeObject(activity));

                context = new TurnContext(this, activity);

                if (alexaRequest.Session.Attributes != null && alexaRequest.Session.Attributes.Any())
                {
                    context.TurnState.Add("AlexaSessionAttributes", alexaRequest.Session.Attributes);
                }
                else
                {
                    context.TurnState.Add("AlexaSessionAttributes", new Dictionary<string, string>());
                }

                context.TurnState.Add("AlexaResponseDirectives", new List<IAlexaDirective>());

                Responses = new Dictionary<string, List<Activity>>();

                await base.RunPipelineAsync(context, callback, default(CancellationToken)).ConfigureAwait(false);

                var key = $"{activity.Conversation.Id}:{activity.Id}";

                try
                {
                    AlexaResponseBody response = null;
                    var activities = Responses.ContainsKey(key) ? Responses[key] : new List<Activity>();
                    response = CreateResponseFromLastActivity(activities, context);
                    response.SessionAttributes = context.AlexaSessionAttributes();
                    return response;
                }
                finally
                {
                    if (Responses.ContainsKey(key))
                    {
                        Responses.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                await alexaOptions.OnTurnError(context, ex);
                throw;
            }
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken CancellationToken)
        {
            var resourceResponses = new List<ResourceResponse>();

            foreach (var activity in activities)
            {
                switch (activity.Type)
                {
                    case ActivityTypes.Message:
                    case ActivityTypes.EndOfConversation:
                        var conversation = activity.Conversation ?? new ConversationAccount();
                        var key = $"{conversation.Id}:{activity.ReplyToId}";

                        if (Responses.ContainsKey(key))
                        {
                            Responses[key].Add(activity);
                        }
                        else
                        {
                            Responses[key] = new List<Activity> { activity };
                        }

                        break;
                    default:
                        Trace.WriteLine(
                            $"AlexaAdapter.SendActivities(): Activities of type '{activity.Type}' aren't supported.");
                        break;
                }

                resourceResponses.Add(new ResourceResponse(activity.Id));
            }

            return Task.FromResult(resourceResponses.ToArray());
        }

        private static Activity RequestToActivity(AlexaRequestBody skillRequest)
        {
            var system = skillRequest.Context.System;
            
            // var userId = system.User.UserId;
            var userId = MapUserId(system.User.UserId);

            // var conversationId = GetMappedConversationId(skillRequest, system);
            var conversationId = $"{system.Application.ApplicationId}:{userId}";

            var activity = new Activity
            {
                ChannelId = AlexaConstants.AlexaChannelId,
                ServiceUrl = $"{system.ApiEndpoint}?token ={system.ApiAccessToken}",
                Recipient = new ChannelAccount(system.Application.ApplicationId, "skill"),
                From = new ChannelAccount(userId, "user"),
                Conversation = new ConversationAccount(false, "conversation", conversationId),
                Type = skillRequest.Request.Type,
                Id = skillRequest.Request.RequestId,
                Timestamp = DateTime.ParseExact(skillRequest.Request.Timestamp, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Locale = skillRequest.Request.Locale
            };

            switch (activity.Type)
            {
                case AlexaRequestTypes.IntentRequest:
                    activity.Value = (skillRequest.Request as AlexaIntentRequest)?.Intent;
                    activity.Code = (skillRequest.Request as AlexaIntentRequest)?.DialogState.ToString();
                    break;
                case AlexaRequestTypes.SessionEndedRequest:
                    activity.Code = (skillRequest.Request as AlexaSessionEndRequest)?.Reason;
                    activity.Value = (skillRequest.Request as AlexaSessionEndRequest)?.Error;
                    break;
            }

            activity.ChannelData = skillRequest;

            return activity;
        }

        private static string MapUserId(string userId)
        {
            var mappedUserId = string.Empty;

            if (!ConversationIdMap.TryGetValue(userId, out mappedUserId))
            {
                mappedUserId = Guid.NewGuid().ToString();
                ConversationIdMap.TryAdd(userId, mappedUserId);
            }

            return mappedUserId;
        }

        private static string GetMappedConversationId(AlexaRequestBody skillRequest, AlexaSystem system)
        {
            var conversationId = string.Empty;

            var key = $"{system.Application.ApplicationId}:{system.User.UserId}";

            if (!ConversationIdMap.TryGetValue(key, out conversationId))
            {
                conversationId = Guid.NewGuid().ToString();
                ConversationIdMap.TryAdd(key, conversationId);
            }

            return conversationId;
        }

        private AlexaResponseBody CreateResponseFromLastActivity(IEnumerable<Activity> activities, ITurnContext context)
        {
            Trace.TraceInformation("Outgoing bot framework activities:");
            Trace.TraceInformation(JsonConvert.SerializeObject(activities));

            var response = new AlexaResponseBody()
            {
                Version = "1.0",
                Response = new AlexaResponse()
                {
                    ShouldEndSession = context.GetAlexaRequestBody().Request.Type ==
                                       AlexaRequestTypes.SessionEndedRequest
                                       || Options.ShouldEndSessionByDefault
                }
            };

            if (context.GetAlexaRequestBody().Request.Type == AlexaRequestTypes.SessionEndedRequest
                || activities == null || !activities.Any())
            {
                response.Response.OutputSpeech = new AlexaOutputSpeech()
                {
                    Type = AlexaOutputSpeechType.PlainText,
                    Text = string.Empty
                };

                Trace.TraceInformation("Outgoing Alexa response:");
                Trace.TraceInformation(JsonConvert.SerializeObject(response));

                return response;
            }

            var activity = activities.First();

            if (activity.Type == ActivityTypes.EndOfConversation)
            {
                response.Response.ShouldEndSession = true;
            }

            if (!string.IsNullOrEmpty(activity.Speak))
            {
                response.Response.OutputSpeech = new AlexaOutputSpeech()
                {
                    Type = AlexaOutputSpeechType.SSML,
                    Ssml = activity.Speak.Contains("<speak>")
                        ? activity.Speak
                        : $"<speak>{activity.Speak}</speak>",
                };

                if (!string.IsNullOrEmpty(activity.Text))
                {
                    response.Response.OutputSpeech.Text = $"{activity.Text} ";
                }
            }
            else if (!string.IsNullOrEmpty(activity.Text))
            {
                if (response.Response.OutputSpeech == null)
                {
                    response.Response.OutputSpeech = new AlexaOutputSpeech()
                    {
                        Type = AlexaOutputSpeechType.PlainText,
                        Text = activity.Text
                    };
                }
            }

            if (context.TurnState.ContainsKey("AlexaReprompt"))
            {
                var repromptSpeech = context.TurnState.Get<string>("AlexaReprompt");

                response.Response.OutputSpeech = new AlexaOutputSpeech()
                {
                    Type = AlexaOutputSpeechType.SSML,
                    Ssml = repromptSpeech.Contains("<speak>")
                        ? repromptSpeech
                        : $"<speak>{repromptSpeech}</speak>"
                };
            }

            AddDirectivesToResponse(context, response);

            AddCardToResponse(context, response, activity);

            switch (activity.InputHint)
            {
                case InputHints.IgnoringInput:
                    response.Response.ShouldEndSession = true;
                    break;
                case InputHints.ExpectingInput:
                    response.Response.ShouldEndSession = false;
                    break;
                case InputHints.AcceptingInput:
                default:
                    break;
            }

            Trace.TraceInformation("Outgoing Alexa response:");
            Trace.TraceInformation(JsonConvert.SerializeObject(response));

            return response;
        }

        private void AddCardToResponse(ITurnContext context, AlexaResponseBody response, Activity activity)
        {
            if (context.TurnState.ContainsKey("AlexaCard") && context.TurnState["AlexaCard"] is AlexaCard)
            {
                response.Response.Card = context.TurnState.Get<AlexaCard>("AlexaCard");
            }
            else if (Options.TryConvertFirstActivityAttachmentToAlexaCard)
            {
                CreateAlexaCardFromAttachment(activity, response);
            }
        }

        private static void AddDirectivesToResponse(ITurnContext context, AlexaResponseBody response)
        {
            response.Response.Directives = context.AlexaResponseDirectives().Select(a => a).ToArray();
        }

        private static void CreateAlexaCardFromAttachment(Activity activity, AlexaResponseBody response)
        {
            var attachment = activity.Attachments != null && activity.Attachments.Any()
                ? activity.Attachments[0]
                : null;

            if (attachment != null)
            {
                switch (attachment.ContentType)
                {
                    case HeroCard.ContentType:
                    case ThumbnailCard.ContentType:
                        if (attachment.Content is HeroCard)
                        {
                            response.Response.Card = CreateAlexaCardFromHeroCard(attachment);
                        }

                        break;
                    case SigninCard.ContentType:
                        response.Response.Card = new AlexaCard()
                        {
                            Type = AlexaCardType.LinkAccount
                        };
                        break;
                }
            }
        }

        private static AlexaCard CreateAlexaCardFromHeroCard(Attachment attachment)
        {
            if (!(attachment.Content is HeroCard heroCardContent))
                return null;

            AlexaCard alexaCard = null;

            if (heroCardContent.Images != null && heroCardContent.Images.Any())
            {
                alexaCard = new AlexaCard()
                {
                    Type = AlexaCardType.Standard,
                    Image = new AlexaCardImage()
                    {
                        SmallImageUrl = heroCardContent.Images[0].Url,
                        LargeImageUrl = heroCardContent.Images.Count > 1 ? heroCardContent.Images[1].Url : null
                    }
                };

                if (heroCardContent.Title != null)
                {
                    alexaCard.Title = heroCardContent.Title;
                }

                if (heroCardContent.Text != null)
                {
                    alexaCard.Content = heroCardContent.Text;
                }
            }
            else
            {
                alexaCard = new AlexaCard()
                {
                    Type = AlexaCardType.Simple
                };
                if (heroCardContent.Title != null)
                {
                    alexaCard.Title = heroCardContent.Title;
                }

                if (heroCardContent.Text != null)
                {
                    alexaCard.Content = heroCardContent.Text;
                }
            }

            return alexaCard;
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
