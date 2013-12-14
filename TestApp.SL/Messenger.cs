using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace OneInc.PolicyOne.App.Core.Utils
{
    /// <summary>
    /// Represents a thread-safe, weak reference based messenger.
    /// </summary>
    public sealed class Messenger
    {
        private static readonly Lazy<Messenger> _singleton = new Lazy<Messenger>(() => new Messenger(), LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly TimeSpan _purgeInterval = TimeSpan.FromMinutes(1);

        private readonly Dictionary<MessageHandlerKey, List<WeakActionBase>> _handlersMap = new Dictionary<MessageHandlerKey, List<WeakActionBase>>();
        private readonly object _handlersMapSync = new object();
        private DateTime _lastPurgeTime;

        public static Messenger Instance
        {
            get { return _singleton.Value; }
        }

        /// <summary>
        /// Registers a handler for a message with a specific token.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to register for.</typeparam>
        /// <param name="token">And optional token for the handler. This is basically a key that determines if the handler should be invoked or not.</param>
        /// <param name="handler">The handler to register.</param>
        /// <returns>An instance of registration. Keep this reference to keep the registration alive.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="handler"/> is null.
        /// </exception>
        /// <remarks>
        /// The <see cref="Messenger"/> class doesn't hold a strong reference to <paramref name="handler"/>.
        /// The only instance that holds it is the returned <see cref="IMessageHandlerRegistration"/>,
        /// so make sure to keep it so long as the handler should be active.
        /// The messenger does keep a strong reference to <paramref name="token"/>, so do not pass big object graphs here.
        /// </remarks>
        public IMessageHandlerRegistration Register<TMessage>(Action<TMessage> handler, object token = null)
        {
            if(handler == null)
                throw new ArgumentNullException("handler");

            var handlersKey = new MessageHandlerKey(typeof(TMessage), token);

            lock (_handlersMapSync)
            {
                PurgeHandlersIfTime();

                List<WeakActionBase> handlersList;
                if (!_handlersMap.TryGetValue(handlersKey, out handlersList))
                {
                    handlersList = new List<WeakActionBase>();
                    _handlersMap[handlersKey] = handlersList;
                }

                var existingHandler = FindHandlerWeakAction(handlersList, handler);
                if (existingHandler != null)
                {
                    throw new InvalidOperationException("Cannot add the same handler for the same message type and the same token more than once.");
                }

                var weakHandler = new WeakAction<TMessage>(handler);
                handlersList.Add(weakHandler);
            }

            return new MessageHandlerRegistration<TMessage>(handler, handlersKey, this);
        }

        /// <summary>
        /// Sends a message to recipients.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message instance to send.</param>
        /// <param name="token">An optional token of the message.</param>
        /// <remarks>
        /// When this method is called, the messenger invokes handlers registered for the <see cref="TMessage"/> type and
        /// with the same <paramref name="token"/> value (determined by <see cref="object.Equals(object)"/> method).
        /// </remarks>
        public void Send<TMessage>(TMessage message, object token = null)
        {
            var handlersKey = new MessageHandlerKey(typeof(TMessage), token);

            var handlersToInvoke = GetListOfHandlersToInvoke(handlersKey);

            InvokeHandlers(message, handlersToInvoke);
        }

        // ReSharper disable once ReturnTypeCanBeEnumerable.Local
        // Keep this as ICollection to ensure we have no lazy execution here. Lazy execution might break thread-safety.
        private ICollection<WeakActionBase> GetListOfHandlersToInvoke(MessageHandlerKey handlersKey)
        {
            lock (_handlersMapSync)
            {
                PurgeHandlersIfTime();

                List<WeakActionBase> handlersList;
                if (!_handlersMap.TryGetValue(handlersKey, out handlersList))
                {
                    // nothing to do
                    return new List<WeakActionBase>();
                }

                Debug.Assert(handlersList != null, "handlersList != null");
                return handlersList.ToList(); // Make sure to copy the list prior to leaving the lock! The list instance might be modified by another thread otherwise
            }
        }

        private void Unregister<TMessage>(MessageHandlerKey key, Action<TMessage> handler)
        {
            Debug.Assert(key != null, "key != null");
            Debug.Assert(handler != null, "handler != null");

            lock (_handlersMapSync)
            {
                PurgeHandlersIfTime();

                List<WeakActionBase> handlers;
                if (!_handlersMap.TryGetValue(key, out handlers))
                {
                    return;
                }

                var existingHandler = FindHandlerWeakAction(handlers, handler);
                if (existingHandler == null)
                {
                    throw new InvalidOperationException("Unexpected subsequent Unregister call. A handler can only be unregistered once.");
                }

                handlers.Remove(existingHandler);
            }
        }

        #region Thread-unsafe code

        private static void InvokeHandlers<TMessage>(TMessage message, IEnumerable<WeakActionBase> handlersToInvoke)
        {
            Debug.Assert(handlersToInvoke != null, "handlersToInvoke != null");

            foreach (var weakAction in handlersToInvoke)
            {
                var typedWeakAction = weakAction as WeakAction<TMessage>;
                Debug.Assert(typedWeakAction != null, "typedWeakAction != null");

                var handler = typedWeakAction.Action;
                if (handler == null)
                {
                    // this is a dead handler, we'll purge it eventually
                    continue;
                }

                handler(message);
            }
        }

        private void PurgeHandlersIfTime()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPurgeTime < _purgeInterval)
            {
                return;
            }

            foreach (var keyHandlerPair in _handlersMap.ToList())
            {
                var handlers = keyHandlerPair.Value;
                foreach (var weakAction in handlers.ToList())
                {
                    if (!weakAction.IsAlive)
                    {
                        handlers.Remove(weakAction);
                    }
                }

                if (handlers.Count == 0)
                {
                    _handlersMap.Remove(keyHandlerPair.Key);
                }
            }

            _lastPurgeTime = now;
        }

        private static WeakAction<TMessage> FindHandlerWeakAction<TMessage>(IEnumerable<WeakActionBase> handlersList, Action<TMessage> handler)
        {
            Debug.Assert(handlersList != null, "handlersList != null");
            Debug.Assert(handler != null, "handler != null");

            return handlersList.Cast<WeakAction<TMessage>>().SingleOrDefault(x => ReferenceEquals(x.Action, handler));
        }

        #endregion

        private sealed class MessageHandlerRegistration<TMessage> : IMessageHandlerRegistration
        {
            // We intentionally keep a strong reference here because instances of this class are supposed to be kept by message listeners to keep registration alive
            private readonly MessageHandlerKey _handlerKey;
            private readonly Messenger _messenger;

            private readonly object _unregisterSync = new object();
            private Action<TMessage> _handler;

            public MessageHandlerRegistration(Action<TMessage> handler, MessageHandlerKey handlerKey, Messenger messenger)
            {
                Debug.Assert(handler != null, "handler != null");
                Debug.Assert(messenger != null, "messenger != null");
                Debug.Assert(handlerKey != null, "handlerKey != null");

                _handler = handler;
                _handlerKey = handlerKey;
                _messenger = messenger;
            }

            public void Unregister()
            {
                lock (_unregisterSync)
                {
                    if (_handler == null)
                    {
                        return;
                    }

                    _messenger.Unregister(_handlerKey, _handler);
                    _handler = null; // clean the handler to release references to subscription objects
                }
            }
        }

        private sealed class MessageHandlerKey : IEquatable<MessageHandlerKey>
        {
            private readonly Type _messageType;
            private readonly object _token;

            public MessageHandlerKey(Type messageType, object token)
            {
                if(messageType == null)
                    throw new ArgumentNullException("messageType");

                _messageType = messageType;
                _token = token;
            }

            public bool Equals(MessageHandlerKey other)
            {
                if (ReferenceEquals(null, other))
                {
                    return false;
                }

                if (ReferenceEquals(this, other))
                {
                    return true;
                }

                return _messageType == other._messageType && Equals(_token, other._token);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                return obj is MessageHandlerKey && Equals((MessageHandlerKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_messageType.GetHashCode() * 397) ^ (_token != null ? _token.GetHashCode() : 0);
                }
            }
        }

        private abstract class WeakActionBase
        {
            public abstract bool IsAlive { get; }
        }

        private sealed class WeakAction<TMessage> : WeakActionBase
        {
            private readonly WeakReference _actionWeakReference;

            public WeakAction(Action<TMessage> action)
            {
                Debug.Assert(action != null, "action != null");

                _actionWeakReference = new WeakReference(action);
            }

            /// <summary>
            /// WARNING! do not store the value as it might leak.
            /// </summary>
            public Action<TMessage> Action
            {
                get
                {
                    var target = _actionWeakReference.Target;
                    Debug.Assert(target == null || target is Action<TMessage>, "target == null || target is Action<TMessage>");
                    return target as Action<TMessage>;
                }
            }

            public override bool IsAlive
            {
                get { return _actionWeakReference.IsAlive; }
            }
        }
    }

    public interface IMessageHandlerRegistration
    {
        void Unregister();
    }
}
