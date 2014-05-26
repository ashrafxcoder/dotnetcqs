using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;

namespace DotNetCqs.Autofac
{
    /// <summary>
    ///     Event bus implementation using autofac as a container.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This bus will store all events on disk and execute them on another thread. This means that the events will be
    ///         executed even if the application
    ///         crashes (unless the application crashes during the actual execution of the event).
    ///     </para>
    /// </remarks>
    public class ContainerEventBus : IEventBus
    {
        private readonly IContainer _container;
        private readonly ICqsStorage _cqsStorage;
        private Thread _executionThread;
        private readonly ManualResetEventSlim _jobEvent = new ManualResetEventSlim(false);
        private bool _shutdown;

        public ContainerEventBus(ICqsStorage cqsStorage, IContainer container)
        {
            _cqsStorage = cqsStorage;
            _container = container;
            _executionThread = new Thread(OnExecuteCommand);
        }

        /// <summary>
        ///     Publish a new application event.
        /// </summary>
        /// <typeparam name="TApplicationEvent">Type of event to publish.</typeparam>
        /// <param name="e">Event to publish, must be serializable.</param>
        /// <returns>
        ///     Task triggered once the event has been delivered.
        /// </returns>
        public async Task PublishAsync<TApplicationEvent>(TApplicationEvent e)
            where TApplicationEvent : ApplicationEvent
        {
            await _cqsStorage.PushAsync(e);
            _jobEvent.Set();
        }

        /// <summary>
        /// Start bus (required to start processing queued commands)
        /// </summary>
        public void Start()
        {
            _jobEvent.Reset();
            _shutdown = false;
            _executionThread.Start();
        }

        /// <summary>
        /// Stop processing bus (will wait for the current command to be completed before shutting down)
        /// </summary>
        public void Stop()
        {
            _shutdown = true;
            _jobEvent.Set();
            _executionThread.Join(5000);
        }


        /// <summary>
        ///     A specific handler failed to consume the application event.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         We will not try to invoke the event again as one or more handlers may have consumed the event successfully.
        ///     </para>
        /// </remarks>
        public event EventHandler<EventHandlerFailedEventArgs> HandlerFailed = delegate { };

        /// <summary>
        ///     Bus failed to invoke an event.
        /// </summary>
        public event EventHandler<BusFailedEventArgs> BusFailed = delegate { };

        /// <summary>
        /// 
        /// </summary>
        /// <returns><c>true</c> </returns>
        internal async Task<bool> ExecuteJobAsync()
        {
            var appEvent = await _cqsStorage.PopEventAsync();
            if (appEvent == null)
                return false;


            using (var scope = _container.BeginLifetimeScope())
            {
                var type = typeof (IApplicationEventSubscriber<>).MakeGenericType(appEvent.GetType());
                var collectionType = typeof(IEnumerable<>).MakeGenericType(type);

                var handlers = ((IEnumerable<object>)scope.Resolve(collectionType)).ToList();
                var failures = new List<HandlerFailure>();
                var method = type.GetMethod("HandleAsync");
                foreach (var handler in handlers)
                {
                    try
                    {
                        var task = (Task)method.Invoke(handler, new object[] { appEvent });
                        await task;
                    }
                    catch (Exception exception)
                    {
                        if (exception is TargetInvocationException)
                            exception = exception.InnerException;

                        failures.Add(new HandlerFailure(handler, exception));
                    }
                }

                if (failures.Any())
                {
                    HandlerFailed(this, new EventHandlerFailedEventArgs(appEvent, failures, handlers.Count));
                }
            }

            return true;
        }

        private void OnExecuteCommand()
        {
            while (true)
            {
                _jobEvent.Wait(1000);
                if (_shutdown)
                    return;

                try
                {
                    var task = ExecuteJobAsync();
                    task.Wait();
                    if (!task.Result)
                        _jobEvent.Reset();
                }
                catch (Exception exception)
                {
                    BusFailed(this, new BusFailedEventArgs(exception));
                }
            }
        }
    }
}