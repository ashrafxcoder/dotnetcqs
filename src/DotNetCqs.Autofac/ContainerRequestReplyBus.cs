﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Autofac;

namespace DotNetCqs.Autofac
{
    public class ContainerRequestReplyBus : IRequestReplyBus
    {
        private readonly IContainer _container;

        public ContainerRequestReplyBus(IContainer container)
        {
            _container = container;
        }

        /// <summary>
        /// Invoke a request and wait for the reply
        /// </summary>
        /// <typeparam name="TReply">Type of reply that we should get for the request.</typeparam><param name="request">Request that we want a reply for.</param>
        /// <returns>
        /// Task which will complete once we've got the result (or something failed, like a request wait timeout).
        /// </returns>
        /// <exception cref="T:System.ArgumentNullException">query</exception>
        public async Task<TReply> ExecuteAsync<TReply>(Request<TReply> request)
        {
            var handler = typeof(IRequestHandler<,>).MakeGenericType(request.GetType(), typeof(TReply));
            var collectionType = typeof (IEnumerable<>).MakeGenericType(handler);

            using (var scope = _container.BeginLifetimeScope())
            {
                var result = ((IEnumerable<object>)scope.Resolve(collectionType));
                var handlers = result.ToArray();

                if (handlers.Length == 0)
                    throw new CqsHandlerMissingException(request.GetType());
                if (handlers.Length != 1)
                    throw new OnlyOneHandlerAllowedException(request.GetType());

                try
                {
                    var method = handler.GetMethod("ExecuteAsync");
                    var task = (Task) method.Invoke(handlers[0], new object[] {request});
                    await task;
                    return ((dynamic) task).Result;
                }
                catch (TargetInvocationException exception)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw new Exception("this will never happen");
                }
            }
        }
    }
}