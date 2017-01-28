﻿// -----------------------------------------------------------------------
//  <copyright file="IMailbox.cs" company="Asynkron HB">
//      Copyright (C) 2015-2016 Asynkron HB All rights reserved
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Proto
{
    internal static class MailboxStatus
    {
        public const int Idle = 0;
        public const int Busy = 1;
    }

    public interface IMailbox
    {
        void PostUserMessage(object msg);
        void PostSystemMessage(object msg);
        void RegisterHandlers(IMessageInvoker invoker, IDispatcher dispatcher);
        void Start();
    }

    public interface IMailboxQueue
    {
        bool HasMessages { get; }
        void Push(object message);
        object Pop();
    }

    public class BoundedMailboxQueue : IMailboxQueue
    {
        private readonly MPMCQueue _messages;

        public BoundedMailboxQueue(int size)
        {
            _messages = new MPMCQueue(size);
        }

        public void Push(object message)
        {
            _messages.Enqueue(message);
        }

        public object Pop()
        {
            return _messages.TryDequeue(out var message)
                ? message
                : null;
        }

        public bool HasMessages => _messages.Count > 0;
    }

    public class UnboundedMailboxQueue : IMailboxQueue
    {
        private readonly ConcurrentQueue<object> _messages = new ConcurrentQueue<object>();

        public void Push(object message)
        {
            _messages.Enqueue(message);
        }

        public object Pop()
        {
            object message;
            return _messages.TryDequeue(out message) ? message : null;
        }

        public bool HasMessages => _messages.Count > 0;
    }

    public class DefaultMailbox : IMailbox
    {
        private readonly IMailboxStatistics[] _stats;
        private readonly IMailboxQueue _systemMessages;
        private readonly IMailboxQueue _userMailbox;
        private IDispatcher _dispatcher;
        private IMessageInvoker _invoker;

        private int _status = MailboxStatus.Idle;
        private bool _suspended;

        public DefaultMailbox(IMailboxQueue systemMessages, IMailboxQueue userMailbox, params IMailboxStatistics[] stats)
        {
            _systemMessages = systemMessages;
            _userMailbox = userMailbox;
            _stats = stats ?? Array.Empty<IMailboxStatistics>();
        }

        public void PostUserMessage(object msg)
        {
            _userMailbox.Push(msg);
            for (var i = 0; i < _stats.Length; i++)
            {
                _stats[i].MessagePosted(msg);
            }
            Schedule();
        }

        public void PostSystemMessage(object msg)
        {
            _systemMessages.Push(msg);
            Schedule();
        }

        public void RegisterHandlers(IMessageInvoker invoker, IDispatcher dispatcher)
        {
            _invoker = invoker;
            _dispatcher = dispatcher;
        }

        public void Start()
        {
            for (var i = 0; i < _stats.Length; i++)
            {
                _stats[i].MailboxStarted();
            }
        }

        private async Task RunAsync()
        {
            var t = _dispatcher.Throughput;

            for (var i = 0; i < t; i++)
            {
                var sys = (SystemMessage) _systemMessages.Pop();
                if (sys != null)
                {
                    if (sys is SuspendMailbox)
                    {
                        _suspended = true;
                    }
                    if (sys is ResumeMailbox)
                    {
                        _suspended = false;
                    }
                    await _invoker.InvokeSystemMessageAsync(sys);
                    continue;
                }
                if (_suspended)
                {
                    break;
                }
                var msg = _userMailbox.Pop();
                if (msg != null)
                {
                    await _invoker.InvokeUserMessageAsync(msg);
                    for (var si = 0; si < _stats.Length; si++)
                    {
                        _stats[si].MessageReceived(msg);
                    }
                }
                else
                {
                    break;
                }
            }

            Interlocked.Exchange(ref _status, MailboxStatus.Idle);

            if (_systemMessages.HasMessages || !_suspended && _userMailbox.HasMessages)
            {
                Schedule();
            }
            else
            {
                for (var i = 0; i < _stats.Length; i++)
                {
                    _stats[i].MailboxEmpty();
                }
            }
        }

        protected void Schedule()
        {
            if (Interlocked.Exchange(ref _status, MailboxStatus.Busy) == MailboxStatus.Idle)
            {
                _dispatcher.Schedule(RunAsync);
            }
        }
    }

    public interface IMailboxStatistics
    {
        void MailboxStarted();
        void MessagePosted(object message);
        void MessageReceived(object message);
        void MailboxEmpty();
    }
}