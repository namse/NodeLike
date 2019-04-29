using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NodeLike
{
    public readonly struct MessageAddress
    {

    }

    public enum MessageState
    {
        Asking,
        Answering,
    }

    public interface IRequestBody
    {

    }

    public interface IResponseBody
    {

    }

    public class Message
    {
        public Guid Id { get; }
        public MessageState MessageState { get; set; }

        public MessageAddress Source => MessageState switch {
            MessageState.Asking => RespondentAddress,
            MessageState.Answering => RequesterAddress,
            _ => throw new Exception("Unknown MessageState")
            };

        public MessageAddress Destination => Source.Equals(RequesterAddress) ? RespondentAddress : RequesterAddress;

        public MessageAddress RequesterAddress { get; set; }
        public MessageAddress RespondentAddress { get; set; }

        public IRequestBody RequestBody { get; set; }
        public IResponseBody ResponseBody { get; set; }

    }
    public interface IMiddleware
    {
        Task<bool> OnMessageAsync(Message message);
    }
    public class ProxyMiddleware: IMiddleware
    {
        private readonly MessageAddress _myMessageAddress;

        public ProxyMiddleware(MessageAddress myMessageAddress)
        {
            _myMessageAddress = myMessageAddress;
        }

        public async Task<bool> OnMessageAsync(Message message)
        {
            if (message.Destination.Equals(_myMessageAddress))
            {
                return false;
            }

            try
            {
                await PostOffice.Shared.SendAsync(message);
            }
            catch
            {
                // ignored
            }

            return true;
        }
    }

    public interface IRequestHandler
    {
        bool CanHandle(Message message);
        Task<Message> OnMessageAsync(Message message);
    }

    public class RequestHandlerMiddleware: IMiddleware
    {
        private readonly MessageAddress _myMessageAddress;
        private readonly List<IRequestHandler> _requestHandlers = new List<IRequestHandler>();

        public RequestHandlerMiddleware(MessageAddress myMessageAddress)
        {
            _myMessageAddress = myMessageAddress;
        }

        public void AddRequestHandler(IRequestHandler requestHandler)
        {
            _requestHandlers.Add(requestHandler);
        }

        public async Task<bool> OnMessageAsync(Message message)
        {
            if (!message.RespondentAddress.Equals(_myMessageAddress))
            {
                return false;
            }

            var handler = FindRequestHandler(message);

            if (handler is null)
            {
                return false;
            }

            var response = await handler.OnMessageAsync(message);

            await PostOffice.Shared.SendAsync(response);

            return true;
        }

        private IRequestHandler FindRequestHandler(Message message)
        {
            return _requestHandlers.FirstOrDefault(handler => handler.CanHandle(message));
        }
    }

    public class ResponseHandlerMiddleware: IMiddleware
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _messageWaitingTaskCompletionSources = new ConcurrentDictionary<Guid, TaskCompletionSource<Message>>();
        private static ResponseHandlerMiddleware _shared;

        private ResponseHandlerMiddleware()
        {
        }

        public static ResponseHandlerMiddleware Shared() => _shared ??= new ResponseHandlerMiddleware();

        public Task<Message> WaitResponseAsync(Guid messageId)
        {
            var messageWaitingTaskCompletionSource = new TaskCompletionSource<Message>();
            _messageWaitingTaskCompletionSources.AddOrUpdate(messageId, messageWaitingTaskCompletionSource,
                (_, source) => source);

            return messageWaitingTaskCompletionSource.Task;
        }

        public Task<bool> OnMessageAsync(Message message)
        {
            if (!_messageWaitingTaskCompletionSources.TryGetValue(message.Id, out var messageWaitingTaskCompletionSource))
            {
                return Task.FromResult(false);
            }

            messageWaitingTaskCompletionSource.SetResult(message);

            return Task.FromResult(true);
        }
    }

    public interface IMessageNetworkConnection
    {
        MessageAddress MessageAddress { get; }
        Task<bool> TrySendAsync(Message message);
    }

    public interface IPostman
    {
        Task<bool> TrySendAsync(Message message);
    }

    public class PostOffice
    {
        private static PostOffice _shared;
        public static PostOffice Shared => _shared ??= new PostOffice();

        private readonly List<IPostman> _postmanList = new List<IPostman>();

        public void AddPostman(IPostman postman)
        {
            _postmanList.Add(postman);
        }

        public async Task SendAsync(Message message)
        {
            foreach (var postman in _postmanList)
            {
                var isMessageSent = await postman.TrySendAsync(message);
                if (isMessageSent)
                {
                    return;
                }
            }

            throw new Exception("Every Postman failed to send message");
        }
    }

    public interface IMessageNetworkConnectionCache
    {
        Task<IMessageNetworkConnection> GetMessageNetworkConnectionAsync(in MessageAddress messageDestination);
    }

    public class MessageNetworkConnectionMemoryCache: IMessageNetworkConnectionCache
    {
        private readonly ConcurrentDictionary<MessageAddress, IMessageNetworkConnection> _messageNetworkConnections = new ConcurrentDictionary<MessageAddress, IMessageNetworkConnection>();

        public void AddMessageNetworkConnection(MessageAddress messageAddress,
            IMessageNetworkConnection messageNetworkConnection)
        {
            _messageNetworkConnections.AddOrUpdate(messageAddress, messageNetworkConnection,
                (_, connection) => connection);
        }

        public Task<IMessageNetworkConnection> GetMessageNetworkConnectionAsync(in MessageAddress messageDestination)
        {
            return !_messageNetworkConnections.TryGetValue(messageDestination, out var messageNetworkConnection)
                ? null
                : Task.FromResult(messageNetworkConnection);
        }
    }

    public class CachePostman: IPostman
    {
        private readonly IMessageNetworkConnectionCache _messageNetworkConnectionCache;

        public CachePostman(IMessageNetworkConnectionCache messageNetworkConnectionCache)
        {
            _messageNetworkConnectionCache = messageNetworkConnectionCache;
        }

        public async Task<bool> TrySendAsync(Message message)
        {
            var messageNetworkConnection =
                await _messageNetworkConnectionCache.GetMessageNetworkConnectionAsync(message.Destination);

            if (messageNetworkConnection is null)
            {
                return false;
            }

            return await messageNetworkConnection.TrySendAsync(message);
        }
    }
}
