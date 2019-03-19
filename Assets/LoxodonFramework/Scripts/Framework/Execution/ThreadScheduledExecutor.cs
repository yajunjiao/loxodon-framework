﻿using System;
using System.Collections.Generic;
using System.Threading;
#if NETFX_CORE
using System.Threading.Tasks;
#endif

using Loxodon.Framework.Asynchronous;

namespace Loxodon.Framework.Execution
{
    public class ThreadScheduledExecutor : AbstractExecutor, IScheduledExecutor
    {
        private IComparer<IDelayTask> comparer = new ComparerImpl<IDelayTask>();
        private List<IDelayTask> queue = new List<IDelayTask>();
        private object _lock = new object();
        private bool running = false;

        public ThreadScheduledExecutor()
        {
        }

        public void Start()
        {
            if (this.running)
                return;

            this.running = true;
#if NETFX_CORE
            Task.Factory.StartNew(() =>
            {
                IDelayTask task = null;
                while (running)
                {
                    lock (_lock)
                    {
                        if (queue.Count <= 0)
                        {
                            Monitor.Wait(_lock);
                            continue;
                        }

                        task = queue[0];
                        if (task.Delay.Ticks > 0)
                        {
                            Monitor.Wait(_lock, task.Delay);
                            continue;
                        }

                        queue.RemoveAt(0);
                    }

                    task.Run();
                }
            });
#else
            ThreadPool.QueueUserWorkItem(state =>
            {
                IDelayTask task = null;
                while (running)
                {
                    lock (_lock)
                    {
                        if (queue.Count <= 0)
                        {
                            Monitor.Wait(_lock);
                            continue;
                        }

                        task = queue[0];
                        if (task.Delay.Ticks > 0)
                        {
                            Monitor.Wait(_lock, task.Delay);
                            continue;
                        }

                        queue.RemoveAt(0);
                    }

                    task.Run();
                }
            });
#endif
        }

        public void Stop()
        {
            if (!this.running)
                return;

            lock (_lock)
            {
                this.running = false;
                Monitor.PulseAll(_lock);
            }

            List<IDelayTask> list = new List<IDelayTask>(this.queue);
            foreach (IDelayTask task in list)
            {
                if (task != null && !task.IsDone)
                    task.Cancel();
            }
            this.queue.Clear();
        }

        private void Add(IDelayTask task)
        {

            lock (_lock)
            {
                queue.Add(task);
                queue.Sort(comparer);
                Monitor.PulseAll(_lock);
            }
        }

        private bool Remove(IDelayTask task)
        {
            lock (_lock)
            {
                if (queue.Remove(task))
                {
                    queue.Sort(comparer);
                    Monitor.PulseAll(_lock);
                    return true;
                }
            }
            return false;
        }

        protected virtual void Check()
        {
            if (!this.running)
                throw new RejectedExecutionException("The ScheduledExecutor isn't started.");
        }

        public virtual Asynchronous.IAsyncResult Schedule(Action command, long delay)
        {
            return Schedule(command, new TimeSpan(delay * TimeSpan.TicksPerMillisecond));
        }

        public virtual Asynchronous.IAsyncResult Schedule(Action command, TimeSpan delay)
        {
            this.Check();
            return new OneTimeDelayTask(this, command, delay);
        }

        public virtual IAsyncResult<TResult> Schedule<TResult>(Func<TResult> command, long delay)
        {
            return Schedule(command, new TimeSpan(delay * TimeSpan.TicksPerMillisecond));
        }

        public virtual IAsyncResult<TResult> Schedule<TResult>(Func<TResult> command, TimeSpan delay)
        {
            this.Check();
            return new OneTimeDelayTask<TResult>(this, command, delay);
        }

        public virtual Asynchronous.IAsyncResult ScheduleAtFixedRate(Action command, long initialDelay, long period)
        {
            return ScheduleAtFixedRate(command, new TimeSpan(initialDelay * TimeSpan.TicksPerMillisecond), new TimeSpan(period * TimeSpan.TicksPerMillisecond));
        }

        public virtual Asynchronous.IAsyncResult ScheduleAtFixedRate(Action command, TimeSpan initialDelay, TimeSpan period)
        {
            this.Check();
            return new FixedRateDelayTask(this, command, initialDelay, period);
        }

        public virtual Asynchronous.IAsyncResult ScheduleWithFixedDelay(Action command, long initialDelay, long delay)
        {
            return ScheduleWithFixedDelay(command, new TimeSpan(initialDelay * TimeSpan.TicksPerMillisecond), new TimeSpan(delay * TimeSpan.TicksPerMillisecond));
        }

        public virtual Asynchronous.IAsyncResult ScheduleWithFixedDelay(Action command, TimeSpan initialDelay, TimeSpan delay)
        {
            this.Check();
            return new FixedDelayDelayTask(this, command, initialDelay, delay);
        }

        public virtual void Dispose()
        {
            this.Stop();
        }

        interface IDelayTask : Asynchronous.IAsyncResult
        {
            TimeSpan Delay { get; }

            void Run();
        }

        class OneTimeDelayTask : AsyncResult, IDelayTask
        {
            private long startTime;
            private TimeSpan delay;
            private Action wrappedAction;
            private ThreadScheduledExecutor executor;

            public OneTimeDelayTask(ThreadScheduledExecutor executor, Action command, TimeSpan delay) : base(true)
            {
                this.startTime = DateTime.Now.Ticks;
                this.delay = delay;
                this.executor = executor;
                this.wrappedAction = () =>
                {
                    try
                    {
                        if (this.IsDone)
                        {
                            return;
                        }

                        if (this.IsCancellationRequested)
                        {
                            this.SetCancelled();
                        }
                        else
                        {
                            command();
                            this.SetResult();
                        }
                    }
                    catch (Exception e)
                    {
                        this.SetException(e);
                    }
                };
                this.executor.Add(this);
            }

            public virtual TimeSpan Delay { get { return new TimeSpan(startTime + delay.Ticks - DateTime.Now.Ticks); } }

            public override bool Cancel()
            {
                if (this.IsDone)
                    return false;

                if (!this.executor.Remove(this))
                    return false;

                this.cancellationRequested = true;
                this.SetCancelled();
                return true;
            }

            public virtual void Run()
            {
                try
                {
#if NETFX_CORE
                    Task.Factory.StartNew(wrappedAction);
#else
                    ThreadPool.QueueUserWorkItem(state=>this.wrappedAction());
#endif
                }
                catch (Exception)
                {
                }
            }
        }

        class OneTimeDelayTask<TResult> : AsyncResult<TResult>, IDelayTask
        {
            private long startTime;
            private TimeSpan delay;
            private Action wrappedAction;
            private ThreadScheduledExecutor executor;

            public OneTimeDelayTask(ThreadScheduledExecutor executor, Func<TResult> command, TimeSpan delay)
            {
                this.startTime = DateTime.Now.Ticks;
                this.delay = delay;
                this.executor = executor;
                this.wrappedAction = () =>
                {
                    try
                    {
                        if (this.IsDone)
                        {
                            return;
                        }

                        if (this.IsCancellationRequested)
                        {
                            this.SetCancelled();
                        }
                        else {
                            this.SetResult(command());
                        }
                    }
                    catch (Exception e)
                    {
                        this.SetException(e);
                    }
                };
                this.executor.Add(this);
            }

            public virtual TimeSpan Delay { get { return new TimeSpan(startTime + delay.Ticks - DateTime.Now.Ticks); } }

            public override bool Cancel()
            {
                if (this.IsDone)
                    return false;

                if (!this.executor.Remove(this))
                    return false;

                this.cancellationRequested = true;
                this.SetCancelled();
                return true;
            }

            public virtual void Run()
            {
                try
                {
#if NETFX_CORE
                    Task.Factory.StartNew(wrappedAction);
#else
                    ThreadPool.QueueUserWorkItem(state=>this.wrappedAction());
#endif
                }
                catch (Exception)
                {
                }
            }
        }

        class FixedRateDelayTask : AsyncResult, IDelayTask
        {
            private long startTime;
            private TimeSpan initialDelay;
            private TimeSpan period;
            private ThreadScheduledExecutor executor;
            private Action wrappedAction;
            private int count = 0;

            public FixedRateDelayTask(ThreadScheduledExecutor executor, Action command, TimeSpan initialDelay, TimeSpan period) : base()
            {
                this.startTime = DateTime.Now.Ticks;
                this.initialDelay = initialDelay;
                this.period = period;
                this.executor = executor;

                this.wrappedAction = () =>
                {
                    try
                    {
                        if (this.IsDone)
                            return;

                        if (this.IsCancellationRequested)
                        {
                            this.SetCancelled();
                        }
                        else {
                            Interlocked.Increment(ref count);
                            //count++;
                            this.executor.Add(this);
                            command();
                        }
                    }
                    catch (Exception)
                    {
                    }
                };
                this.executor.Add(this);
            }

            public virtual TimeSpan Delay { get { return new TimeSpan(startTime + initialDelay.Ticks + period.Ticks * count - DateTime.Now.Ticks); } }

            public override bool Cancel()
            {
                if (this.IsDone)
                    return false;

                this.executor.Remove(this);
                this.cancellationRequested = true;
                this.SetCancelled();
                return true;
            }

            public virtual void Run()
            {
                try
                {
#if NETFX_CORE
                    Task.Factory.StartNew(wrappedAction);
#else
                    ThreadPool.QueueUserWorkItem(state=>this.wrappedAction());
#endif
                }
                catch (Exception)
                {
                }
            }
        }

        class FixedDelayDelayTask : AsyncResult, IDelayTask
        {
            //private long startTime;
            private TimeSpan delay;
            //private TimeSpan nextDelay;
            private DateTime nextTime;
            private ThreadScheduledExecutor executor;
            private Action wrappedAction;

            public FixedDelayDelayTask(ThreadScheduledExecutor executor, Action command, TimeSpan initialDelay, TimeSpan delay) : base()
            {
                //this.startTime = DateTime.Now.Ticks;
                this.delay = delay;
                this.executor = executor;
                //this.nextDelay = initialDelay;
                this.nextTime = DateTime.Now + initialDelay;

                this.wrappedAction = () =>
                {
                    try
                    {
                        if (this.IsDone)
                            return;

                        if (this.IsCancellationRequested)
                        {
                            this.SetCancelled();
                        }
                        else
                        {
                            command();
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        if (this.IsCancellationRequested)
                        {
                            this.SetCancelled();
                        }
                        else {
                            //this.nextDelay = this.nextDelay.Add(this.delay);
                            this.nextTime = DateTime.Now + this.delay;
                            this.executor.Add(this);
                        }
                    }
                };
                this.executor.Add(this);
            }

            public virtual TimeSpan Delay { get { return this.nextTime - DateTime.Now; } }

            public override bool Cancel()
            {
                if (this.IsDone)
                    return false;

                this.executor.Remove(this);
                this.cancellationRequested = true;
                this.SetCancelled();
                return true;
            }

            public virtual void Run()
            {
                try
                {
#if NETFX_CORE
                    Task.Factory.StartNew(wrappedAction);
#else
                    ThreadPool.QueueUserWorkItem(state=>this.wrappedAction());
#endif
                }
                catch (Exception)
                {
                }
            }
        }

        class ComparerImpl<T> : IComparer<T> where T : IDelayTask
        {
            public int Compare(T x, T y)
            {
                if (x.Delay.Ticks == y.Delay.Ticks)
                    return 0;

                return x.Delay.Ticks > y.Delay.Ticks ? 1 : -1;
            }
        }
    }
}