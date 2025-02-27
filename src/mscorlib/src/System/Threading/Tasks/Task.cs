﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
//
//
// A schedulable unit of work.
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.Permissions;
using System.Threading;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Win32;
using System.Diagnostics.Tracing;

// Disable the "reference to volatile field not treated as volatile" error.
#pragma warning disable 0420

namespace System.Threading.Tasks
{

    /// <summary>
    /// Utility class for allocating structs as heap variables
    /// </summary>
    internal class Shared<T>
    {
        internal T Value;

        internal Shared(T value)
        {
            this.Value = value;
        }

    }

    /// <summary>
    /// Represents the current stage in the lifecycle of a <see cref="Task"/>.
    /// </summary>
    public enum TaskStatus
    {
        /// <summary> 
        /// The task has been initialized but has not yet been scheduled.
        /// </summary>
        Created,
        /// <summary> 
        /// The task is waiting to be activated and scheduled internally by the .NET Framework infrastructure.
        /// </summary>
        WaitingForActivation,
        /// <summary>
        /// The task has been scheduled for execution but has not yet begun executing.
        /// </summary>
        WaitingToRun,
        /// <summary>
        /// The task is running but has not yet completed.
        /// </summary>
        Running,
        // /// <summary>
        // /// The task is currently blocked in a wait state.
        // /// </summary>
        // Blocked,
        /// <summary>
        /// The task has finished executing and is implicitly waiting for
        /// attached child tasks to complete.
        /// </summary>
        WaitingForChildrenToComplete,
        /// <summary>
        /// The task completed execution successfully.
        /// </summary>
        RanToCompletion,
        /// <summary>
        /// The task acknowledged cancellation by throwing an OperationCanceledException with its own CancellationToken
        /// while the token was in signaled state, or the task's CancellationToken was already signaled before the
        /// task started executing.
        /// </summary>
        Canceled,
        /// <summary>
        /// The task completed due to an unhandled exception.
        /// </summary>
        Faulted
    }

    /// <summary>
    /// Represents an asynchronous operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Task"/> instances may be created in a variety of ways. The most common approach is by
    /// using the Task type's <see cref="Factory"/> property to retrieve a <see
    /// cref="System.Threading.Tasks.TaskFactory"/> instance that can be used to create tasks for several
    /// purposes. For example, to create a <see cref="Task"/> that runs an action, the factory's StartNew
    /// method may be used:
    /// <code>
    /// // C# 
    /// var t = Task.Factory.StartNew(() => DoAction());
    /// 
    /// ' Visual Basic 
    /// Dim t = Task.Factory.StartNew(Function() DoAction())
    /// </code>
    /// </para>
    /// <para>
    /// The <see cref="Task"/> class also provides constructors that initialize the Task but that do not
    /// schedule it for execution. For performance reasons, TaskFactory's StartNew method should be the
    /// preferred mechanism for creating and scheduling computational tasks, but for scenarios where creation
    /// and scheduling must be separated, the constructors may be used, and the task's <see cref="Start()"/>
    /// method may then be used to schedule the task for execution at a later time.
    /// </para>
    /// <para>
    /// All members of <see cref="Task"/>, except for <see cref="Dispose()"/>, are thread-safe
    /// and may be used from multiple threads concurrently.
    /// </para>
    /// <para>
    /// For operations that return values, the <see cref="System.Threading.Tasks.Task{TResult}"/> class
    /// should be used.
    /// </para>
    /// <para>
    /// For developers implementing custom debuggers, several internal and private members of Task may be
    /// useful (these may change from release to release). The Int32 m_taskId field serves as the backing
    /// store for the <see cref="Id"/> property, however accessing this field directly from a debugger may be
    /// more efficient than accessing the same value through the property's getter method (the
    /// s_taskIdCounter Int32 counter is used to retrieve the next available ID for a Task). Similarly, the
    /// Int32 m_stateFlags field stores information about the current lifecycle stage of the Task,
    /// information also accessible through the <see cref="Status"/> property. The m_action System.Object
    /// field stores a reference to the Task's delegate, and the m_stateObject System.Object field stores the
    /// async state passed to the Task by the developer. Finally, for debuggers that parse stack frames, the
    /// InternalWait method serves a potential marker for when a Task is entering a wait operation.
    /// </para>
    /// </remarks>
    [HostProtection(Synchronization = true, ExternalThreading = true)]
    [DebuggerTypeProxy(typeof(SystemThreadingTasks_TaskDebugView))]
    [DebuggerDisplay("Id = {Id}, Status = {Status}, Method = {DebuggerDisplayMethodDescription}")]
    public class Task : IThreadPoolWorkItem, IAsyncResult, IDisposable
    {
        [ThreadStatic]
        internal static Task t_currentTask;  // The currently executing task.
        [ThreadStatic]
        private static StackGuard t_stackGuard;  // The stack guard object for this thread

        internal static int s_taskIdCounter; //static counter used to generate unique task IDs
        private readonly static TaskFactory s_factory = new TaskFactory();

        private volatile int m_taskId; // this task's unique ID. initialized only if it is ever requested

        internal object m_action;    // The body of the task.  Might be Action<object>, Action<TState> or Action.  Or possibly a Func.
        // If m_action is set to null it will indicate that we operate in the
        // "externally triggered completion" mode, which is exclusively meant 
        // for the signalling Task<TResult> (aka. promise). In this mode,
        // we don't call InnerInvoke() in response to a Wait(), but simply wait on
        // the completion event which will be set when the Future class calls Finish().
        // But the event would now be signalled if Cancel() is called


        internal object m_stateObject; // A state object that can be optionally supplied, passed to action.
        internal TaskScheduler m_taskScheduler; // The task scheduler this task runs under. 

        internal readonly Task m_parent; // A task's parent, or null if parent-less.


        internal volatile int m_stateFlags;

        // State constants for m_stateFlags;
        // The bits of m_stateFlags are allocated as follows:
        //   0x40000000 - TaskBase state flag
        //   0x3FFF0000 - Task state flags
        //   0x0000FF00 - internal TaskCreationOptions flags
        //   0x000000FF - publicly exposed TaskCreationOptions flags
        //
        // See TaskCreationOptions for bit values associated with TaskCreationOptions
        //
        private const int OptionsMask = 0xFFFF; // signifies the Options portion of m_stateFlags bin: 0000 0000 0000 0000 1111 1111 1111 1111
        internal const int TASK_STATE_STARTED = 0x10000;                                       //bin: 0000 0000 0000 0001 0000 0000 0000 0000
        internal const int TASK_STATE_DELEGATE_INVOKED = 0x20000;                              //bin: 0000 0000 0000 0010 0000 0000 0000 0000
        internal const int TASK_STATE_DISPOSED = 0x40000;                                      //bin: 0000 0000 0000 0100 0000 0000 0000 0000
        internal const int TASK_STATE_EXCEPTIONOBSERVEDBYPARENT = 0x80000;                     //bin: 0000 0000 0000 1000 0000 0000 0000 0000
        internal const int TASK_STATE_CANCELLATIONACKNOWLEDGED = 0x100000;                     //bin: 0000 0000 0001 0000 0000 0000 0000 0000
        internal const int TASK_STATE_FAULTED = 0x200000;                                      //bin: 0000 0000 0010 0000 0000 0000 0000 0000
        internal const int TASK_STATE_CANCELED = 0x400000;                                     //bin: 0000 0000 0100 0000 0000 0000 0000 0000
        internal const int TASK_STATE_WAITING_ON_CHILDREN = 0x800000;                          //bin: 0000 0000 1000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;                           //bin: 0000 0001 0000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_WAITINGFORACTIVATION = 0x2000000;                        //bin: 0000 0010 0000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_COMPLETION_RESERVED = 0x4000000;                         //bin: 0000 0100 0000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_THREAD_WAS_ABORTED = 0x8000000;                          //bin: 0000 1000 0000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_WAIT_COMPLETION_NOTIFICATION = 0x10000000;               //bin: 0001 0000 0000 0000 0000 0000 0000 0000
        //This could be moved to InternalTaskOptions enum
        internal const int TASK_STATE_EXECUTIONCONTEXT_IS_NULL = 0x20000000;                   //bin: 0010 0000 0000 0000 0000 0000 0000 0000
        internal const int TASK_STATE_TASKSCHEDULED_WAS_FIRED = 0x40000000;                    //bin: 0100 0000 0000 0000 0000 0000 0000 0000

        // A mask for all of the final states a task may be in
        private const int TASK_STATE_COMPLETED_MASK = TASK_STATE_CANCELED | TASK_STATE_FAULTED | TASK_STATE_RAN_TO_COMPLETION;

        // Values for ContingentProperties.m_internalCancellationRequested.
        private const int CANCELLATION_REQUESTED = 0x1;

        // Can be null, a single continuation, a list of continuations, or s_taskCompletionSentinel,
        // in that order. The logic arround this object assumes it will never regress to a previous state.
        private volatile object m_continuationObject = null;

        // m_continuationObject is set to this when the task completes.
        private static readonly object s_taskCompletionSentinel = new object();

        // A private flag that would be set (only) by the debugger
        // When true the Async Causality logging trace is enabled as well as a dictionary to relate operation ids with Tasks
        [FriendAccessAllowed]
        internal static bool s_asyncDebuggingEnabled; //false by default

        // This dictonary relates the task id, from an operation id located in the Async Causality log to the actual
        // task. This is to be used by the debugger ONLY. Task in this dictionary represent current active tasks.
        private static readonly Dictionary<int, Task> s_currentActiveTasks = new Dictionary<int, Task>();
        private static readonly Object s_activeTasksLock = new Object();

        // These methods are a way to access the dictionary both from this class and for other classes that also
        // activate dummy tasks. Specifically the AsyncTaskMethodBuilder and AsyncTaskMethodBuilder<>
        [FriendAccessAllowed]
        internal static bool AddToActiveTasks(Task task)
        {
            Contract.Requires(task != null, "Null Task objects can't be added to the ActiveTasks collection");
            lock (s_activeTasksLock)
            {
                s_currentActiveTasks[task.Id] = task;
            }
            //always return true to keep signature as bool for backwards compatibility
            return true;
        }

        [FriendAccessAllowed]
        internal static void RemoveFromActiveTasks(int taskId)
        {
            lock (s_activeTasksLock)
            {
                s_currentActiveTasks.Remove(taskId);
            }
        }

        // We moved a number of Task properties into this class.  The idea is that in most cases, these properties never
        // need to be accessed during the life cycle of a Task, so we don't want to instantiate them every time.  Once
        // one of these properties needs to be written, we will instantiate a ContingentProperties object and set
        // the appropriate property.
        internal class ContingentProperties
        {
            // Additional context

            internal ExecutionContext m_capturedContext; // The execution context to run the task within, if any.

            // Completion fields (exceptions and event)

            internal volatile ManualResetEventSlim m_completionEvent; // Lazily created if waiting is required.
            internal volatile TaskExceptionHolder m_exceptionsHolder; // Tracks exceptions, if any have occurred

            // Cancellation fields (token, registration, and internally requested)

            internal CancellationToken m_cancellationToken; // Task's cancellation token, if it has one
            internal Shared<CancellationTokenRegistration> m_cancellationRegistration; // Task's registration with the cancellation token
            internal volatile int m_internalCancellationRequested; // Its own field because multiple threads legally try to set it.

            // Parenting fields

            // # of active children + 1 (for this task itself).
            // Used for ensuring all children are done before this task can complete
            // The extra count helps prevent the race condition for executing the final state transition
            // (i.e. whether the last child or this task itself should call FinishStageTwo())
            internal volatile int m_completionCountdown = 1;
            // A list of child tasks that threw an exception (TCEs don't count),
            // but haven't yet been waited on by the parent, lazily initialized.
            internal volatile List<Task> m_exceptionalChildren;

            /// <summary>
            /// Sets the internal completion event.
            /// </summary>
            internal void SetCompleted()
            {
                var mres = m_completionEvent;
                if (mres != null) mres.Set();
            }

            /// <summary>
            /// Checks if we registered a CT callback during construction, and deregisters it. 
            /// This should be called when we know the registration isn't useful anymore. Specifically from Finish() if the task has completed
            /// successfully or with an exception.
            /// </summary>
            internal void DeregisterCancellationCallback()
            {
                if (m_cancellationRegistration != null)
                {
                    // Harden against ODEs thrown from disposing of the CTR.
                    // Since the task has already been put into a final state by the time this
                    // is called, all we can do here is suppress the exception.
                    try { m_cancellationRegistration.Value.Dispose(); }
                    catch (ObjectDisposedException) { }
                    m_cancellationRegistration = null;
                }
            }
        }


        // This field will only be instantiated to some non-null value if any ContingentProperties need to be set.
        // This will be a ContingentProperties instance or a type derived from it
        internal volatile ContingentProperties m_contingentProperties;

        // Special internal constructor to create an already-completed task.
        // if canceled==true, create a Canceled task, or else create a RanToCompletion task.
        // Constructs the task as already completed
        internal Task(bool canceled, TaskCreationOptions creationOptions, CancellationToken ct)
        {
            int optionFlags = (int)creationOptions;
            if (canceled)
            {
                m_stateFlags = TASK_STATE_CANCELED | TASK_STATE_CANCELLATIONACKNOWLEDGED | optionFlags;
                ContingentProperties props;
                m_contingentProperties = props = new ContingentProperties(); // can't have children, so just instantiate directly
                props.m_cancellationToken = ct;
                props.m_internalCancellationRequested = CANCELLATION_REQUESTED;
            }
            else
                m_stateFlags = TASK_STATE_RAN_TO_COMPLETION | optionFlags;
        }

        // Uncomment if/when we want Task.FromException
        //// Special internal constructor to create an already-Faulted task.
        //internal Task(Exception exception)
        //{
        //    Contract.Assert(exception != null);
        //    ContingentProperties props;
        //    m_contingentProperties = props = new ContingentProperties(); // can't have children, so just instantiate directly
        //    props.m_exceptionsHolder.Add(exception);
        //    m_stateFlags = TASK_STATE_FAULTED;
        //}

        /// <summary>Constructor for use with promise-style tasks that aren't configurable.</summary>
        internal Task()
        {
            m_stateFlags = TASK_STATE_WAITINGFORACTIVATION | (int)InternalTaskOptions.PromiseTask;
        }

        // Special constructor for use with promise-style tasks.
        // Added promiseStyle parameter as an aid to the compiler to distinguish between (state,TCO) and
        // (action,TCO).  It should always be true.
        internal Task(object state, TaskCreationOptions creationOptions, bool promiseStyle)
        {
            Contract.Assert(promiseStyle, "Promise CTOR: promiseStyle was false");

            // Check the creationOptions. We allow the AttachedToParent option to be specified for promise tasks.
            // Also allow RunContinuationsAsynchronously because this is the constructor called by TCS
            if ((creationOptions & ~(TaskCreationOptions.AttachedToParent | TaskCreationOptions.RunContinuationsAsynchronously)) != 0)
            {
                throw new ArgumentOutOfRangeException("creationOptions");
            }

            // m_parent is readonly, and so must be set in the constructor.
            // Only set a parent if AttachedToParent is specified.
            if ((creationOptions & TaskCreationOptions.AttachedToParent) != 0)
                m_parent = Task.InternalCurrent;

            TaskConstructorCore(null, state, default(CancellationToken), creationOptions, InternalTaskOptions.PromiseTask, null);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the Task.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="action"/> argument is null.</exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action action)
            : this(action, null, null, default(CancellationToken), TaskCreationOptions.None, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action and <see cref="System.Threading.CancellationToken">CancellationToken</see>.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the Task.</param>
        /// <param name="cancellationToken">The <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// that will be assigned to the new Task.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="action"/> argument is null.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action action, CancellationToken cancellationToken)
            : this(action, null, null, cancellationToken, TaskCreationOptions.None, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action and creation options.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="creationOptions">
        /// The <see cref="System.Threading.Tasks.TaskCreationOptions">TaskCreationOptions</see> used to
        /// customize the Task's behavior.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="creationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskCreationOptions"/>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action action, TaskCreationOptions creationOptions)
            : this(action, null, Task.InternalCurrentIfAttached(creationOptions), default(CancellationToken), creationOptions, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action and creation options.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new task.</param>
        /// <param name="creationOptions">
        /// The <see cref="System.Threading.Tasks.TaskCreationOptions">TaskCreationOptions</see> used to
        /// customize the Task's behavior.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="creationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskCreationOptions"/>.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action action, CancellationToken cancellationToken, TaskCreationOptions creationOptions)
            : this(action, null, Task.InternalCurrentIfAttached(creationOptions), cancellationToken, creationOptions, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }


        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action and state.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="state">An object representing data to be used by the action.</param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action<object> action, object state)
            : this(action, state, null, default(CancellationToken), TaskCreationOptions.None, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action, state, snd options.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="state">An object representing data to be used by the action.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new task.</param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action<object> action, object state, CancellationToken cancellationToken)
            : this(action, state, null, cancellationToken, TaskCreationOptions.None, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action, state, snd options.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="state">An object representing data to be used by the action.</param>
        /// <param name="creationOptions">
        /// The <see cref="System.Threading.Tasks.TaskCreationOptions">TaskCreationOptions</see> used to
        /// customize the Task's behavior.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="creationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskCreationOptions"/>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action<object> action, object state, TaskCreationOptions creationOptions)
            : this(action, state, Task.InternalCurrentIfAttached(creationOptions), default(CancellationToken), creationOptions, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// Initializes a new <see cref="Task"/> with the specified action, state, snd options.
        /// </summary>
        /// <param name="action">The delegate that represents the code to execute in the task.</param>
        /// <param name="state">An object representing data to be used by the action.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new task.</param>
        /// <param name="creationOptions">
        /// The <see cref="System.Threading.Tasks.TaskCreationOptions">TaskCreationOptions</see> used to
        /// customize the Task's behavior.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="creationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskCreationOptions"/>.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task(Action<object> action, object state, CancellationToken cancellationToken, TaskCreationOptions creationOptions)
            : this(action, state, Task.InternalCurrentIfAttached(creationOptions), cancellationToken, creationOptions, InternalTaskOptions.None, null)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }

        internal Task(Action<object> action, object state, Task parent, CancellationToken cancellationToken,
            TaskCreationOptions creationOptions, InternalTaskOptions internalOptions, TaskScheduler scheduler, ref StackCrawlMark stackMark)
            : this(action, state, parent, cancellationToken, creationOptions, internalOptions, scheduler)
        {
            PossiblyCaptureContext(ref stackMark);
        }

        /// <summary>
        /// An internal constructor used by the factory methods on task and its descendent(s).
        /// This variant does not capture the ExecutionContext; it is up to the caller to do that.
        /// </summary>
        /// <param name="action">An action to execute.</param>
        /// <param name="state">Optional state to pass to the action.</param>
        /// <param name="parent">Parent of Task.</param>
        /// <param name="cancellationToken">A CancellationToken for the task.</param>
        /// <param name="scheduler">A task scheduler under which the task will run.</param>
        /// <param name="creationOptions">Options to control its execution.</param>
        /// <param name="internalOptions">Internal options to control its execution</param>
        internal Task(Delegate action, object state, Task parent, CancellationToken cancellationToken,
            TaskCreationOptions creationOptions, InternalTaskOptions internalOptions, TaskScheduler scheduler)
        {
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }
            Contract.EndContractBlock();

            // This is readonly, and so must be set in the constructor
            // Keep a link to your parent if: (A) You are attached, or (B) you are self-replicating.
            if (((creationOptions & TaskCreationOptions.AttachedToParent) != 0) ||
                ((internalOptions & InternalTaskOptions.SelfReplicating) != 0)
                )
            {
                m_parent = parent;
            }

            TaskConstructorCore(action, state, cancellationToken, creationOptions, internalOptions, scheduler);
        }

        /// <summary>
        /// Common logic used by the following internal ctors:
        ///     Task()
        ///     Task(object action, object state, Task parent, TaskCreationOptions options, TaskScheduler taskScheduler)
        /// </summary>
        /// <param name="action">Action for task to execute.</param>
        /// <param name="state">Object to which to pass to action (may be null)</param>
        /// <param name="scheduler">Task scheduler on which to run thread (only used by continuation tasks).</param>
        /// <param name="cancellationToken">A CancellationToken for the Task.</param>
        /// <param name="creationOptions">Options to customize behavior of Task.</param>
        /// <param name="internalOptions">Internal options to customize behavior of Task.</param>
        internal void TaskConstructorCore(object action, object state, CancellationToken cancellationToken,
            TaskCreationOptions creationOptions, InternalTaskOptions internalOptions, TaskScheduler scheduler)
        {
            m_action = action;
            m_stateObject = state;
            m_taskScheduler = scheduler;

            // Check for validity of options
            if ((creationOptions &
                    ~(TaskCreationOptions.AttachedToParent |
                      TaskCreationOptions.LongRunning |
                      TaskCreationOptions.DenyChildAttach |
                      TaskCreationOptions.HideScheduler |
                      TaskCreationOptions.PreferFairness |
                      TaskCreationOptions.RunContinuationsAsynchronously)) != 0)
            {
                throw new ArgumentOutOfRangeException("creationOptions");
            }

#if DEBUG
            // Check the validity of internalOptions
            int illegalInternalOptions = 
                    (int) (internalOptions &
                            ~(InternalTaskOptions.SelfReplicating |
                              InternalTaskOptions.ChildReplica |
                              InternalTaskOptions.PromiseTask |
                              InternalTaskOptions.ContinuationTask |
                              InternalTaskOptions.LazyCancellation |
                              InternalTaskOptions.QueuedByRuntime));
            Contract.Assert(illegalInternalOptions == 0, "TaskConstructorCore: Illegal internal options");
#endif

            // Throw exception if the user specifies both LongRunning and SelfReplicating
            if (((creationOptions & TaskCreationOptions.LongRunning) != 0) &&
                ((internalOptions & InternalTaskOptions.SelfReplicating) != 0))
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_ctor_LRandSR"));
            }

            // Assign options to m_stateAndOptionsFlag.
            Contract.Assert(m_stateFlags == 0, "TaskConstructorCore: non-zero m_stateFlags");
            Contract.Assert((((int)creationOptions) | OptionsMask) == OptionsMask, "TaskConstructorCore: options take too many bits");
            var tmpFlags = (int)creationOptions | (int)internalOptions;
            if ((m_action == null) || ((internalOptions & InternalTaskOptions.ContinuationTask) != 0))
            {
                // For continuation tasks or TaskCompletionSource.Tasks, begin life in the 
                // WaitingForActivation state rather than the Created state.
                tmpFlags |= TASK_STATE_WAITINGFORACTIVATION;
            }
            m_stateFlags = tmpFlags; // one write to the volatile m_stateFlags instead of two when setting the above options

            // Now is the time to add the new task to the children list 
            // of the creating task if the options call for it.
            // We can safely call the creator task's AddNewChild() method to register it, 
            // because at this point we are already on its thread of execution.

            if (m_parent != null
                && ((creationOptions & TaskCreationOptions.AttachedToParent) != 0)
                && ((m_parent.CreationOptions & TaskCreationOptions.DenyChildAttach) == 0)
                )
            {
                m_parent.AddNewChild();
            }

            // if we have a non-null cancellationToken, allocate the contingent properties to save it
            // we need to do this as the very last thing in the construction path, because the CT registration could modify m_stateFlags
            if (cancellationToken.CanBeCanceled)
            {
                Contract.Assert((internalOptions &
                    (InternalTaskOptions.ChildReplica | InternalTaskOptions.SelfReplicating | InternalTaskOptions.ContinuationTask)) == 0,
                    "TaskConstructorCore: Did not expect to see cancelable token for replica/replicating or continuation task.");

                AssignCancellationToken(cancellationToken, null, null);
            }
        }

        /// <summary>
        /// Handles everything needed for associating a CancellationToken with a task which is being constructed.
        /// This method is meant to be be called either from the TaskConstructorCore or from ContinueWithCore.
        /// </summary>
        private void AssignCancellationToken(CancellationToken cancellationToken, Task antecedent, TaskContinuation continuation)
        {
            // There is no need to worry about concurrency issues here because we are in the constructor path of the task --
            // there should not be any race conditions to set m_contingentProperties at this point.
            ContingentProperties props = EnsureContingentPropertiesInitialized(needsProtection: false);
            props.m_cancellationToken = cancellationToken;

            try
            {
                if (AppContextSwitches.ThrowExceptionIfDisposedCancellationTokenSource)
                {
                    cancellationToken.ThrowIfSourceDisposed();
                }

                // If an unstarted task has a valid CancellationToken that gets signalled while the task is still not queued
                // we need to proactively cancel it, because it may never execute to transition itself. 
                // The only way to accomplish this is to register a callback on the CT.
                // We exclude Promise tasks from this, because TaskCompletionSource needs to fully control the inner tasks's lifetime (i.e. not allow external cancellations)                
                if ((((InternalTaskOptions)Options &
                    (InternalTaskOptions.QueuedByRuntime | InternalTaskOptions.PromiseTask | InternalTaskOptions.LazyCancellation)) == 0))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Fast path for an already-canceled cancellationToken
                        this.InternalCancel(false);
                    }
                    else
                    {
                        // Regular path for an uncanceled cancellationToken
                        CancellationTokenRegistration ctr;
                        if (antecedent == null)
                        {
                            // if no antecedent was specified, use this task's reference as the cancellation state object
                            ctr = cancellationToken.InternalRegisterWithoutEC(s_taskCancelCallback, this);
                        }
                        else
                        {
                            // If an antecedent was specified, pack this task, its antecedent and the TaskContinuation together as a tuple 
                            // and use it as the cancellation state object. This will be unpacked in the cancellation callback so that 
                            // antecedent.RemoveCancellation(continuation) can be invoked.
                            ctr = cancellationToken.InternalRegisterWithoutEC(s_taskCancelCallback,
                                                                              new Tuple<Task, Task, TaskContinuation>(this, antecedent, continuation));
                        }

                        props.m_cancellationRegistration = new Shared<CancellationTokenRegistration>(ctr);
                    }
                }
            }
            catch
            {
                // If we have an exception related to our CancellationToken, then we need to subtract ourselves
                // from our parent before throwing it.
                if ((m_parent != null) &&
                    ((Options & TaskCreationOptions.AttachedToParent) != 0)
                     && ((m_parent.Options & TaskCreationOptions.DenyChildAttach) == 0))
                {
                    m_parent.DisregardChild();
                }
                throw;
            }
        }


        // Static delegate to be used as a cancellation callback on unstarted tasks that have a valid cancellation token.
        // This is necessary to transition them into canceled state if their cancellation token is signalled while they are still not queued
        private readonly static Action<Object> s_taskCancelCallback = new Action<Object>(TaskCancelCallback);
        private static void TaskCancelCallback(Object o)
        {
            var targetTask = o as Task;
            if (targetTask == null)
            {
                var tuple = o as Tuple<Task, Task, TaskContinuation>;
                if (tuple != null)
                {
                    targetTask = tuple.Item1;

                    Task antecedentTask = tuple.Item2;
                    TaskContinuation continuation = tuple.Item3;
                    antecedentTask.RemoveContinuation(continuation);
                }
            }
            Contract.Assert(targetTask != null,
                "targetTask should have been non-null, with the supplied argument being a task or a tuple containing one");
            targetTask.InternalCancel(false);
        }

        // Debugger support
        private string DebuggerDisplayMethodDescription
        {
            get
            {
                Delegate d = (Delegate)m_action;
                return d != null ? d.Method.ToString() : "{null}";
            }
        }


        /// <summary>
        /// Captures the ExecutionContext so long as flow isn't suppressed.
        /// </summary>
        /// <param name="stackMark">A stack crawl mark pointing to the frame of the caller.</param>

        [SecuritySafeCritical]
        internal void PossiblyCaptureContext(ref StackCrawlMark stackMark)
        {
            Contract.Assert(m_contingentProperties == null || m_contingentProperties.m_capturedContext == null,
                "Captured an ExecutionContext when one was already captured.");

            // In the legacy .NET 3.5 build, we don't have the optimized overload of Capture()
            // available, so we call the parameterless overload.
            CapturedContext = ExecutionContext.Capture(
                ref stackMark,
                ExecutionContext.CaptureOptions.IgnoreSyncCtx | ExecutionContext.CaptureOptions.OptimizeDefaultCase);
        }

        // Internal property to process TaskCreationOptions access and mutation.
        internal TaskCreationOptions Options
        {
            get
            {
                int stateFlags = m_stateFlags; // "cast away" volatility to enable inlining of OptionsMethod
                return OptionsMethod(stateFlags);
            }
        }

        // Similar to Options property, but allows for the use of a cached flags value rather than
        // a read of the volatile m_stateFlags field.
        internal static TaskCreationOptions OptionsMethod(int flags)
        {
            Contract.Assert((OptionsMask & 1) == 1, "OptionsMask needs a shift in Options.get");
            return (TaskCreationOptions)(flags & OptionsMask);
        }

        // Atomically OR-in newBits to m_stateFlags, while making sure that
        // no illegalBits are set.  Returns true on success, false on failure.
        internal bool AtomicStateUpdate(int newBits, int illegalBits)
        {
            // This could be implemented in terms of:
            //     internal bool AtomicStateUpdate(int newBits, int illegalBits, ref int oldFlags);
            // but for high-throughput perf, that delegation's cost is noticeable.

            SpinWait sw = new SpinWait();
            do
            {
                int oldFlags = m_stateFlags;
                if ((oldFlags & illegalBits) != 0) return false;
                if (Interlocked.CompareExchange(ref m_stateFlags, oldFlags | newBits, oldFlags) == oldFlags)
                {
                    return true;
                }
                sw.SpinOnce();
            } while (true);
        }

        internal bool AtomicStateUpdate(int newBits, int illegalBits, ref int oldFlags)
        {
            SpinWait sw = new SpinWait();
            do
            {
                oldFlags = m_stateFlags;
                if ((oldFlags & illegalBits) != 0) return false;
                if (Interlocked.CompareExchange(ref m_stateFlags, oldFlags | newBits, oldFlags) == oldFlags)
                {
                    return true;
                }
                sw.SpinOnce();
            } while (true);
        }

        /// <summary>
        /// Sets or clears the TASK_STATE_WAIT_COMPLETION_NOTIFICATION state bit.
        /// The debugger sets this bit to aid it in "stepping out" of an async method body.
        /// If enabled is true, this must only be called on a task that has not yet been completed.
        /// If enabled is false, this may be called on completed tasks.
        /// Either way, it should only be used for promise-style tasks.
        /// </summary>
        /// <param name="enabled">true to set the bit; false to unset the bit.</param>
        internal void SetNotificationForWaitCompletion(bool enabled)
        {
            Contract.Assert((Options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) != 0,
                "Should only be used for promise-style tasks"); // hasn't been vetted on other kinds as there hasn't been a need

            if (enabled)
            {
                // Atomically set the END_AWAIT_NOTIFICATION bit
                bool success = AtomicStateUpdate(TASK_STATE_WAIT_COMPLETION_NOTIFICATION,
                                  TASK_STATE_COMPLETED_MASK | TASK_STATE_COMPLETION_RESERVED);
                Contract.Assert(success, "Tried to set enabled on completed Task");
            }
            else
            {
                // Atomically clear the END_AWAIT_NOTIFICATION bit
                SpinWait sw = new SpinWait();
                while (true)
                {
                    int oldFlags = m_stateFlags;
                    int newFlags = oldFlags & (~TASK_STATE_WAIT_COMPLETION_NOTIFICATION);
                    if (Interlocked.CompareExchange(ref m_stateFlags, newFlags, oldFlags) == oldFlags) break;
                    sw.SpinOnce();
                }
            }
        }

        /// <summary>
        /// Calls the debugger notification method if the right bit is set and if
        /// the task itself allows for the notification to proceed.
        /// </summary>
        /// <returns>true if the debugger was notified; otherwise, false.</returns>
        internal bool NotifyDebuggerOfWaitCompletionIfNecessary()
        {
            // Notify the debugger if of any of the tasks we've waited on requires notification
            if (IsWaitNotificationEnabled && ShouldNotifyDebuggerOfWaitCompletion)
            {
                NotifyDebuggerOfWaitCompletion();
                return true;
            }
            return false;
        }

        /// <summary>Returns true if any of the supplied tasks require wait notification.</summary>
        /// <param name="tasks">The tasks to check.</param>
        /// <returns>true if any of the tasks require notification; otherwise, false.</returns>
        internal static bool AnyTaskRequiresNotifyDebuggerOfWaitCompletion(Task[] tasks)
        {
            Contract.Assert(tasks != null, "Expected non-null array of tasks");
            foreach (var task in tasks)
            {
                if (task != null &&
                    task.IsWaitNotificationEnabled &&
                    task.ShouldNotifyDebuggerOfWaitCompletion) // potential recursion
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Gets whether either the end await bit is set or (not xor) the task has not completed successfully.</summary>
        /// <returns>(DebuggerBitSet || !RanToCompletion)</returns>
        internal bool IsWaitNotificationEnabledOrNotRanToCompletion
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return (m_stateFlags & (Task.TASK_STATE_WAIT_COMPLETION_NOTIFICATION | Task.TASK_STATE_RAN_TO_COMPLETION))
                        != Task.TASK_STATE_RAN_TO_COMPLETION;
            }
        }

        /// <summary>
        /// Determines whether we should inform the debugger that we're ending a join with a task.  
        /// This should only be called if the debugger notification bit is set, as it is has some cost,
        /// namely it is a virtual call (however calling it if the bit is not set is not functionally 
        /// harmful).  Derived implementations may choose to only conditionally call down to this base 
        /// implementation.
        /// </summary>
        internal virtual bool ShouldNotifyDebuggerOfWaitCompletion // ideally would be familyAndAssembly, but that can't be done in C#
        {
            get
            {
                // It's theoretically possible but extremely rare that this assert could fire because the 
                // bit was unset between the time that it was checked and this method was called.
                // It's so remote a chance that it's worth having the assert to protect against misuse.
                bool isWaitNotificationEnabled = IsWaitNotificationEnabled;
                Contract.Assert(isWaitNotificationEnabled, "Should only be called if the wait completion bit is set.");
                return isWaitNotificationEnabled;
            }
        }

        /// <summary>Gets whether the task's debugger notification for wait completion bit is set.</summary>
        /// <returns>true if the bit is set; false if it's not set.</returns>
        internal bool IsWaitNotificationEnabled // internal only to enable unit tests; would otherwise be private
        {
            get { return (m_stateFlags & TASK_STATE_WAIT_COMPLETION_NOTIFICATION) != 0; }
        }

        /// <summary>Placeholder method used as a breakpoint target by the debugger.  Must not be inlined or optimized.</summary>
        /// <remarks>All joins with a task should end up calling this if their debugger notification bit is set.</remarks>
        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        private void NotifyDebuggerOfWaitCompletion()
        {
            // It's theoretically possible but extremely rare that this assert could fire because the 
            // bit was unset between the time that it was checked and this method was called.
            // It's so remote a chance that it's worth having the assert to protect against misuse.
            Contract.Assert(IsWaitNotificationEnabled, "Should only be called if the wait completion bit is set.");

            // Now that we're notifying the debugger, clear the bit.  The debugger should do this anyway,
            // but this adds a bit of protection in case it fails to, and given that the debugger is involved, 
            // the overhead here for the interlocked is negligable.  We do still rely on the debugger
            // to clear bits, as this doesn't recursively clear bits in the case of, for example, WhenAny.
            SetNotificationForWaitCompletion(enabled: false);
        }


        // Atomically mark a Task as started while making sure that it is not canceled.
        internal bool MarkStarted()
        {
            return AtomicStateUpdate(TASK_STATE_STARTED, TASK_STATE_CANCELED | TASK_STATE_STARTED);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool FireTaskScheduledIfNeeded(TaskScheduler ts)
        {
            var etwLog = TplEtwProvider.Log;
            if (etwLog.IsEnabled() && (m_stateFlags & Task.TASK_STATE_TASKSCHEDULED_WAS_FIRED) == 0)
            {
                m_stateFlags |= Task.TASK_STATE_TASKSCHEDULED_WAS_FIRED;

                Task currentTask = Task.InternalCurrent;
                Task parentTask = this.m_parent;
                etwLog.TaskScheduled(ts.Id, currentTask == null ? 0 : currentTask.Id,
                                     this.Id, parentTask == null ? 0 : parentTask.Id, (int)this.Options,
                                     System.Threading.Thread.GetDomainID());
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// Internal function that will be called by a new child task to add itself to 
        /// the children list of the parent (this).
        /// 
        /// Since a child task can only be created from the thread executing the action delegate
        /// of this task, reentrancy is neither required nor supported. This should not be called from
        /// anywhere other than the task construction/initialization codepaths.
        /// </summary>
        internal void AddNewChild()
        {
            Contract.Assert(Task.InternalCurrent == this || this.IsSelfReplicatingRoot, "Task.AddNewChild(): Called from an external context");

            var props = EnsureContingentPropertiesInitialized(needsProtection: true);

            if (props.m_completionCountdown == 1 && !IsSelfReplicatingRoot)
            {
                // A count of 1 indicates so far there was only the parent, and this is the first child task
                // Single kid => no fuss about who else is accessing the count. Let's save ourselves 100 cycles
                // We exclude self replicating root tasks from this optimization, because further child creation can take place on 
                // other cores and with bad enough timing this write may not be visible to them.
                props.m_completionCountdown++;
            }
            else
            {
                // otherwise do it safely
                Interlocked.Increment(ref props.m_completionCountdown);
            }
        }

        // This is called in the case where a new child is added, but then encounters a CancellationToken-related exception.
        // We need to subtract that child from m_completionCountdown, or the parent will never complete.
        internal void DisregardChild()
        {
            Contract.Assert(Task.InternalCurrent == this, "Task.DisregardChild(): Called from an external context");

            var props = EnsureContingentPropertiesInitialized(needsProtection: true);
            Contract.Assert(props.m_completionCountdown >= 2, "Task.DisregardChild(): Expected parent count to be >= 2");
            Interlocked.Decrement(ref props.m_completionCountdown);
        }

        /// <summary>
        /// Starts the <see cref="Task"/>, scheduling it for execution to the current <see
        /// cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see>.
        /// </summary>
        /// <remarks>
        /// A task may only be started and run only once.  Any attempts to schedule a task a second time
        /// will result in an exception.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The <see cref="Task"/> is not in a valid state to be started. It may have already been started,
        /// executed, or canceled, or it may have been created in a manner that doesn't support direct
        /// scheduling.
        /// </exception>
        public void Start()
        {
            Start(TaskScheduler.Current);
        }

        /// <summary>
        /// Starts the <see cref="Task"/>, scheduling it for execution to the specified <see
        /// cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see>.
        /// </summary>
        /// <remarks>
        /// A task may only be started and run only once. Any attempts to schedule a task a second time will
        /// result in an exception.
        /// </remarks>
        /// <param name="scheduler">
        /// The <see cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see> with which to associate
        /// and execute this task.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The <see cref="Task"/> is not in a valid state to be started. It may have already been started,
        /// executed, or canceled, or it may have been created in a manner that doesn't support direct
        /// scheduling.
        /// </exception>
        public void Start(TaskScheduler scheduler)
        {
            // Read the volatile m_stateFlags field once and cache it for subsequent operations
            int flags = m_stateFlags;

            // Need to check this before (m_action == null) because completed tasks will
            // set m_action to null.  We would want to know if this is the reason that m_action == null.
            if (IsCompletedMethod(flags))
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_Start_TaskCompleted"));
            }

            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }

            var options = OptionsMethod(flags);
            if ((options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) != 0)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_Start_Promise"));
            }
            if ((options & (TaskCreationOptions)InternalTaskOptions.ContinuationTask) != 0)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_Start_ContinuationTask"));
            }

            // Make sure that Task only gets started once.  Or else throw an exception.
            if (Interlocked.CompareExchange(ref m_taskScheduler, scheduler, null) != null)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_Start_AlreadyStarted"));
            }

            ScheduleAndStart(true);
        }

        /// <summary>
        /// Runs the <see cref="Task"/> synchronously on the current <see
        /// cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A task may only be started and run only once. Any attempts to schedule a task a second time will
        /// result in an exception.
        /// </para>
        /// <para>
        /// Tasks executed with <see cref="RunSynchronously()"/> will be associated with the current <see
        /// cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see>.
        /// </para>
        /// <para>
        /// If the target scheduler does not support running this Task on the current thread, the Task will
        /// be scheduled for execution on the scheduler, and the current thread will block until the
        /// Task has completed execution.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The <see cref="Task"/> is not in a valid state to be started. It may have already been started,
        /// executed, or canceled, or it may have been created in a manner that doesn't support direct
        /// scheduling.
        /// </exception>
        public void RunSynchronously()
        {
            InternalRunSynchronously(TaskScheduler.Current, waitForCompletion: true);
        }

        /// <summary>
        /// Runs the <see cref="Task"/> synchronously on the <see
        /// cref="System.Threading.Tasks.TaskScheduler">scheduler</see> provided.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A task may only be started and run only once. Any attempts to schedule a task a second time will
        /// result in an exception.
        /// </para>
        /// <para>
        /// If the target scheduler does not support running this Task on the current thread, the Task will
        /// be scheduled for execution on the scheduler, and the current thread will block until the
        /// Task has completed execution.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The <see cref="Task"/> is not in a valid state to be started. It may have already been started,
        /// executed, or canceled, or it may have been created in a manner that doesn't support direct
        /// scheduling.
        /// </exception>
        /// <exception cref="ArgumentNullException">The <paramref name="scheduler"/> parameter
        /// is null.</exception>
        /// <param name="scheduler">The scheduler on which to attempt to run this task inline.</param>
        public void RunSynchronously(TaskScheduler scheduler)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            InternalRunSynchronously(scheduler, waitForCompletion: true);
        }

        //
        // Internal version of RunSynchronously that allows not waiting for completion.
        // 
        [SecuritySafeCritical] // Needed for QueueTask
        internal void InternalRunSynchronously(TaskScheduler scheduler, bool waitForCompletion)
        {
            Contract.Requires(scheduler != null, "Task.InternalRunSynchronously(): null TaskScheduler");

            // Read the volatile m_stateFlags field once and cache it for subsequent operations
            int flags = m_stateFlags;

            // Can't call this method on a continuation task
            var options = OptionsMethod(flags);
            if ((options & (TaskCreationOptions)InternalTaskOptions.ContinuationTask) != 0)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_RunSynchronously_Continuation"));
            }

            // Can't call this method on a promise-style task
            if ((options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) != 0)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_RunSynchronously_Promise"));
            }

            // Can't call this method on a task that has already completed
            if (IsCompletedMethod(flags))
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_RunSynchronously_TaskCompleted"));
            }

            // Make sure that Task only gets started once.  Or else throw an exception.
            if (Interlocked.CompareExchange(ref m_taskScheduler, scheduler, null) != null)
            {
                throw new InvalidOperationException(Environment.GetResourceString("Task_RunSynchronously_AlreadyStarted"));
            }

            // execute only if we successfully cancel when concurrent cancel attempts are made.
            // otherwise throw an exception, because we've been canceled.
            if (MarkStarted())
            {
                bool taskQueued = false;
                try
                {
                    // We wrap TryRunInline() in a try/catch block and move an excepted task to Faulted here,
                    // but not in Wait()/WaitAll()/FastWaitAll().  Here, we know for sure that the
                    // task will not be subsequently scheduled (assuming that the scheduler adheres
                    // to the guideline that an exception implies that no state change took place),
                    // so it is safe to catch the exception and move the task to a final state.  The
                    // same cannot be said for Wait()/WaitAll()/FastWaitAll().
                    if (!scheduler.TryRunInline(this, false))
                    {
                        scheduler.InternalQueueTask(this);
                        taskQueued = true; // only mark this after successfully queuing the task.
                    }

                    // A successful TryRunInline doesn't guarantee completion, as there may be unfinished children.
                    // Also if we queued the task above, the task may not be done yet.
                    if (waitForCompletion && !IsCompleted)
                    {
                        SpinThenBlockingWait(Timeout.Infinite, default(CancellationToken));
                    }
                }
                catch (Exception e)
                {
                    // we 1) either received an unexpected exception originating from a custom scheduler, which needs to be wrapped in a TSE and thrown
                    //    2) or a a ThreadAbortException, which we need to skip here, because it would already have been handled in Task.Execute
                    if (!taskQueued && !(e is ThreadAbortException))
                    {
                        // We had a problem with TryRunInline() or QueueTask().  
                        // Record the exception, marking ourselves as Completed/Faulted.
                        TaskSchedulerException tse = new TaskSchedulerException(e);
                        AddException(tse);
                        Finish(false);

                        // Mark ourselves as "handled" to avoid crashing the finalizer thread if the caller neglects to
                        // call Wait() on this task.
                        // m_contingentProperties.m_exceptionsHolder *should* already exist after AddException()
                        Contract.Assert(
                            (m_contingentProperties != null) &&
                            (m_contingentProperties.m_exceptionsHolder != null) &&
                            (m_contingentProperties.m_exceptionsHolder.ContainsFaultList),
                            "Task.InternalRunSynchronously(): Expected m_contingentProperties.m_exceptionsHolder to exist " +
                            "and to have faults recorded.");
                        m_contingentProperties.m_exceptionsHolder.MarkAsHandled(false);

                        // And re-throw.
                        throw tse;
                    }
                    // We had a problem with waiting or this is a thread abort.  Just re-throw.
                    else throw;
                }
            }
            else
            {
                Contract.Assert((m_stateFlags & TASK_STATE_CANCELED) != 0, "Task.RunSynchronously: expected TASK_STATE_CANCELED to be set");
                // Can't call this method on canceled task.
                throw new InvalidOperationException(Environment.GetResourceString("Task_RunSynchronously_TaskCompleted"));
            }
        }


        ////
        //// Helper methods for Factory StartNew methods.
        ////


        // Implicitly converts action to object and handles the meat of the StartNew() logic.
        internal static Task InternalStartNew(
            Task creatingTask, Delegate action, object state, CancellationToken cancellationToken, TaskScheduler scheduler,
            TaskCreationOptions options, InternalTaskOptions internalOptions, ref StackCrawlMark stackMark)
        {
            // Validate arguments.
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            // Create and schedule the task. This throws an InvalidOperationException if already shut down.
            // Here we add the InternalTaskOptions.QueuedByRuntime to the internalOptions, so that TaskConstructorCore can skip the cancellation token registration
            Task t = new Task(action, state, creatingTask, cancellationToken, options, internalOptions | InternalTaskOptions.QueuedByRuntime, scheduler);
            t.PossiblyCaptureContext(ref stackMark);

            t.ScheduleAndStart(false);
            return t;
        }

        /// <summary>
        /// Gets a unique ID for a <see cref="Task">Task</see> or task continuation instance.
        /// </summary>
        internal static int NewId()
        {
            int newId = 0;
            // We need to repeat if Interlocked.Increment wraps around and returns 0.
            // Otherwise next time this task's Id is queried it will get a new value
            do
            {
                newId = Interlocked.Increment(ref s_taskIdCounter);
            }
            while (newId == 0);
            TplEtwProvider.Log.NewID(newId);
            return newId;
        }


        /////////////
        // properties

        /// <summary>
        /// Gets a unique ID for this <see cref="Task">Task</see> instance.
        /// </summary>
        /// <remarks>
        /// Task IDs are assigned on-demand and do not necessarily represent the order in the which Task
        /// instances were created.
        /// </remarks>
        public int Id
        {
            get
            {
                if (m_taskId == 0)
                {
                    int newId = NewId();
                    Interlocked.CompareExchange(ref m_taskId, newId, 0);
                }

                return m_taskId;
            }
        }

        /// <summary>
        /// Returns the unique ID of the currently executing <see cref="Task">Task</see>.
        /// </summary>
        public static int? CurrentId
        {
            get
            {
                Task currentTask = InternalCurrent;
                if (currentTask != null)
                    return currentTask.Id;
                else
                    return null;
            }
        }

        /// <summary>
        /// Gets the <see cref="Task">Task</see> instance currently executing, or
        /// null if none exists.
        /// </summary>
        internal static Task InternalCurrent
        {
            get { return t_currentTask; }
        }

        /// <summary>
        /// Gets the Task instance currently executing if the specified creation options
        /// contain AttachedToParent.
        /// </summary>
        /// <param name="options">The options to check.</param>
        /// <returns>The current task if there is one and if AttachToParent is in the options; otherwise, null.</returns>
        internal static Task InternalCurrentIfAttached(TaskCreationOptions creationOptions)
        {
            return (creationOptions & TaskCreationOptions.AttachedToParent) != 0 ? InternalCurrent : null;
        }

        /// <summary>
        /// Gets the StackGuard object assigned to the current thread.
        /// </summary>
        internal static StackGuard CurrentStackGuard
        {
            get
            {
                StackGuard sg = t_stackGuard;
                if (sg == null)
                {
                    t_stackGuard = sg = new StackGuard();
                }
                return sg;
            }
        }


        /// <summary>
        /// Gets the <see cref="T:System.AggregateException">Exception</see> that caused the <see
        /// cref="Task">Task</see> to end prematurely. If the <see
        /// cref="Task">Task</see> completed successfully or has not yet thrown any
        /// exceptions, this will return null.
        /// </summary>
        /// <remarks>
        /// Tasks that throw unhandled exceptions store the resulting exception and propagate it wrapped in a
        /// <see cref="System.AggregateException"/> in calls to <see cref="Wait()">Wait</see>
        /// or in accesses to the <see cref="Exception"/> property.  Any exceptions not observed by the time
        /// the Task instance is garbage collected will be propagated on the finalizer thread.
        /// </remarks>
        public AggregateException Exception
        {
            get
            {
                AggregateException e = null;

                // If you're faulted, retrieve the exception(s)
                if (IsFaulted) e = GetExceptions(false);

                // Only return an exception in faulted state (skip manufactured exceptions)
                // A "benevolent" race condition makes it possible to return null when IsFaulted is
                // true (i.e., if IsFaulted is set just after the check to IsFaulted above).
                Contract.Assert((e == null) || IsFaulted, "Task.Exception_get(): returning non-null value when not Faulted");

                return e;
            }
        }

        /// <summary>
        /// Gets the <see cref="T:System.Threading.Tasks.TaskStatus">TaskStatus</see> of this Task. 
        /// </summary>
        public TaskStatus Status
        {
            get
            {
                TaskStatus rval;

                // get a cached copy of the state flags.  This should help us
                // to get a consistent view of the flags if they are changing during the
                // execution of this method.
                int sf = m_stateFlags;

                if ((sf & TASK_STATE_FAULTED) != 0) rval = TaskStatus.Faulted;
                else if ((sf & TASK_STATE_CANCELED) != 0) rval = TaskStatus.Canceled;
                else if ((sf & TASK_STATE_RAN_TO_COMPLETION) != 0) rval = TaskStatus.RanToCompletion;
                else if ((sf & TASK_STATE_WAITING_ON_CHILDREN) != 0) rval = TaskStatus.WaitingForChildrenToComplete;
                else if ((sf & TASK_STATE_DELEGATE_INVOKED) != 0) rval = TaskStatus.Running;
                else if ((sf & TASK_STATE_STARTED) != 0) rval = TaskStatus.WaitingToRun;
                else if ((sf & TASK_STATE_WAITINGFORACTIVATION) != 0) rval = TaskStatus.WaitingForActivation;
                else rval = TaskStatus.Created;

                return rval;
            }
        }

        /// <summary>
        /// Gets whether this <see cref="Task">Task</see> instance has completed
        /// execution due to being canceled.
        /// </summary>
        /// <remarks>
        /// A <see cref="Task">Task</see> will complete in Canceled state either if its <see cref="CancellationToken">CancellationToken</see> 
        /// was marked for cancellation before the task started executing, or if the task acknowledged the cancellation request on 
        /// its already signaled CancellationToken by throwing an 
        /// <see cref="System.OperationCanceledException">OperationCanceledException</see> that bears the same 
        /// <see cref="System.Threading.CancellationToken">CancellationToken</see>.
        /// </remarks>
        public bool IsCanceled
        {
            get
            {
                // Return true if canceled bit is set and faulted bit is not set
                return (m_stateFlags & (TASK_STATE_CANCELED | TASK_STATE_FAULTED)) == TASK_STATE_CANCELED;
            }
        }

        /// <summary>
        /// Returns true if this task has a cancellation token and it was signaled.
        /// To be used internally in execute entry codepaths.
        /// </summary>
        internal bool IsCancellationRequested
        {
            get
            {
                // check both the internal cancellation request flag and the CancellationToken attached to this task
                var props = m_contingentProperties;
                return props != null &&
                    (props.m_internalCancellationRequested == CANCELLATION_REQUESTED ||
                     props.m_cancellationToken.IsCancellationRequested);
            }
        }

        /// <summary>
        /// Ensures that the contingent properties field has been initialized.
        /// ASSUMES THAT m_stateFlags IS ALREADY SET!
        /// </summary>
        /// <param name="needsProtection">true if this needs to be done in a thread-safe manner; otherwise, false.</param>
        /// <returns>The initialized contingent properties object.</returns>
        internal ContingentProperties EnsureContingentPropertiesInitialized(bool needsProtection)
        {
            var props = m_contingentProperties;
            return props != null ? props : EnsureContingentPropertiesInitializedCore(needsProtection);
        }

        /// <summary>
        /// Initializes the contingent properties object.  This assumes a check has already been done for nullness.
        /// </summary>
        /// <param name="needsProtection">true if this needs to be done in a thread-safe manner; otherwise, false.</param>
        /// <returns>The initialized contingent properties object.</returns>
        private ContingentProperties EnsureContingentPropertiesInitializedCore(bool needsProtection)
        {
            if (needsProtection)
            {
                return LazyInitializer.EnsureInitialized<ContingentProperties>(ref m_contingentProperties, s_createContingentProperties);
            }
            else
            {
                Contract.Assert(m_contingentProperties == null, "Expected props to be null after checking and with needsProtection == false");
                return m_contingentProperties = new ContingentProperties();
            }
        }

        // Cached functions for lazily initializing contingent properties
        private static readonly Func<ContingentProperties> s_createContingentProperties = () => new ContingentProperties();

        /// <summary>
        /// This internal property provides access to the CancellationToken that was set on the task 
        /// when it was constructed.
        /// </summary>
        internal CancellationToken CancellationToken
        {
            get
            {
                var props = m_contingentProperties;
                return (props == null) ? default(CancellationToken) : props.m_cancellationToken;
            }
        }

        /// <summary>
        /// Gets whether this <see cref="Task"/> threw an OperationCanceledException while its CancellationToken was signaled.
        /// </summary>
        internal bool IsCancellationAcknowledged
        {
            get { return (m_stateFlags & TASK_STATE_CANCELLATIONACKNOWLEDGED) != 0; }
        }


        /// <summary>
        /// Gets whether this <see cref="Task">Task</see> has completed.
        /// </summary>
        /// <remarks>
        /// <see cref="IsCompleted"/> will return true when the Task is in one of the three
        /// final states: <see cref="System.Threading.Tasks.TaskStatus.RanToCompletion">RanToCompletion</see>,
        /// <see cref="System.Threading.Tasks.TaskStatus.Faulted">Faulted</see>, or
        /// <see cref="System.Threading.Tasks.TaskStatus.Canceled">Canceled</see>.
        /// </remarks>
        public bool IsCompleted
        {
            get
            {
                int stateFlags = m_stateFlags; // enable inlining of IsCompletedMethod by "cast"ing away the volatility
                return IsCompletedMethod(stateFlags);
            }
        }

        // Similar to IsCompleted property, but allows for the use of a cached flags value
        // rather than reading the volatile m_stateFlags field.
        private static bool IsCompletedMethod(int flags)
        {
            return (flags & TASK_STATE_COMPLETED_MASK) != 0;
        }

        // For use in InternalWait -- marginally faster than (Task.Status == TaskStatus.RanToCompletion)
        internal bool IsRanToCompletion
        {
            get { return (m_stateFlags & TASK_STATE_COMPLETED_MASK) == TASK_STATE_RAN_TO_COMPLETION; }
        }

        /// <summary>
        /// Gets the <see cref="T:System.Threading.Tasks.TaskCreationOptions">TaskCreationOptions</see> used
        /// to create this task.
        /// </summary>
        public TaskCreationOptions CreationOptions
        {
            get { return Options & (TaskCreationOptions)(~InternalTaskOptions.InternalOptionsMask); }
        }

        /// <summary>
        /// Gets a <see cref="T:System.Threading.WaitHandle"/> that can be used to wait for the task to
        /// complete.
        /// </summary>
        /// <remarks>
        /// Using the wait functionality provided by <see cref="Wait()"/>
        /// should be preferred over using <see cref="IAsyncResult.AsyncWaitHandle"/> for similar
        /// functionality.
        /// </remarks>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The <see cref="Task"/> has been disposed.
        /// </exception>
        WaitHandle IAsyncResult.AsyncWaitHandle
        {
            // Although a slim event is used internally to avoid kernel resource allocation, this function
            // forces allocation of a true WaitHandle when called.
            get
            {
                bool isDisposed = (m_stateFlags & TASK_STATE_DISPOSED) != 0;
                if (isDisposed)
                {
                    throw new ObjectDisposedException(null, Environment.GetResourceString("Task_ThrowIfDisposed"));
                }
                return CompletedEvent.WaitHandle;
            }
        }

        /// <summary>
        /// Gets the state object supplied when the <see cref="Task">Task</see> was created,
        /// or null if none was supplied.
        /// </summary>
        public object AsyncState
        {
            get { return m_stateObject; }
        }

        /// <summary>
        /// Gets an indication of whether the asynchronous operation completed synchronously.
        /// </summary>
        /// <value>true if the asynchronous operation completed synchronously; otherwise, false.</value>
        bool IAsyncResult.CompletedSynchronously
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Provides access to the TaskScheduler responsible for executing this Task.
        /// </summary>
        internal TaskScheduler ExecutingTaskScheduler
        {
            get { return m_taskScheduler; }
        }

        /// <summary>
        /// Provides access to factory methods for creating <see cref="Task"/> and <see cref="Task{TResult}"/> instances.
        /// </summary>
        /// <remarks>
        /// The factory returned from <see cref="Factory"/> is a default instance
        /// of <see cref="System.Threading.Tasks.TaskFactory"/>, as would result from using
        /// the default constructor on TaskFactory.
        /// </remarks>
        public static TaskFactory Factory { get { return s_factory; } }

        /// <summary>A task that's already been completed successfully.</summary>
        private static Task s_completedTask;

        /// <summary>Gets a task that's already been completed successfully.</summary>
        /// <remarks>May not always return the same instance.</remarks>        
        public static Task CompletedTask
        {
            get
            {
                var completedTask = s_completedTask;
                if (completedTask == null)
                    s_completedTask = completedTask = new Task(false, (TaskCreationOptions)InternalTaskOptions.DoNotDispose, default(CancellationToken)); // benign initialization race condition
                return completedTask;
            }
        }

        /// <summary>
        /// Provides an event that can be used to wait for completion.
        /// Only called by IAsyncResult.AsyncWaitHandle, which means that we really do need to instantiate a completion event.
        /// </summary>
        internal ManualResetEventSlim CompletedEvent
        {
            get
            {
                var contingentProps = EnsureContingentPropertiesInitialized(needsProtection: true);
                if (contingentProps.m_completionEvent == null)
                {
                    bool wasCompleted = IsCompleted;
                    ManualResetEventSlim newMre = new ManualResetEventSlim(wasCompleted);
                    if (Interlocked.CompareExchange(ref contingentProps.m_completionEvent, newMre, null) != null)
                    {
                        // Someone else already set the value, so we will just close the event right away.
                        newMre.Dispose();
                    }
                    else if (!wasCompleted && IsCompleted)
                    {
                        // We published the event as unset, but the task has subsequently completed.
                        // Set the event's state properly so that callers don't deadlock.
                        newMre.Set();
                    }
                }

                return contingentProps.m_completionEvent;
            }
        }

        /// <summary>
        /// Determines whether this is the root task of a self replicating group.
        /// </summary>
        internal bool IsSelfReplicatingRoot
        {
            get
            {
                // Return true if self-replicating bit is set and child replica bit is not set
                return (Options & (TaskCreationOptions)(InternalTaskOptions.SelfReplicating | InternalTaskOptions.ChildReplica))
                    == (TaskCreationOptions)InternalTaskOptions.SelfReplicating;
            }
        }

        /// <summary>
        /// Determines whether the task is a replica itself.
        /// </summary>
        internal bool IsChildReplica
        {
            get { return (Options & (TaskCreationOptions)InternalTaskOptions.ChildReplica) != 0; }
        }

        internal int ActiveChildCount
        {
            get
            {
                var props = m_contingentProperties;
                return props != null ? props.m_completionCountdown - 1 : 0;
            }
        }

        /// <summary>
        /// The property formerly known as IsFaulted.
        /// </summary>
        internal bool ExceptionRecorded
        {
            get
            {
                var props = m_contingentProperties;
                return (props != null) && (props.m_exceptionsHolder != null) && (props.m_exceptionsHolder.ContainsFaultList);
            }
        }

        /// <summary>
        /// Gets whether the <see cref="Task"/> completed due to an unhandled exception.
        /// </summary>
        /// <remarks>
        /// If <see cref="IsFaulted"/> is true, the Task's <see cref="Status"/> will be equal to
        /// <see cref="System.Threading.Tasks.TaskStatus.Faulted">TaskStatus.Faulted</see>, and its
        /// <see cref="Exception"/> property will be non-null.
        /// </remarks>
        public bool IsFaulted
        {
            get
            {
                // Faulted is "king" -- if that bit is present (regardless of other bits), we are faulted.
                return ((m_stateFlags & TASK_STATE_FAULTED) != 0);
            }
        }

        /// <summary>
        /// The captured execution context for the current task to run inside
        /// If the TASK_STATE_EXECUTIONCONTEXT_IS_NULL flag is set, this means ExecutionContext.Capture returned null, otherwise
        /// If the captured context is the default, nothing is saved, otherwise the m_contingentProperties inflates to save the context
        /// </summary>
        internal ExecutionContext CapturedContext
        {
            get
            {
                if ((m_stateFlags & TASK_STATE_EXECUTIONCONTEXT_IS_NULL) == TASK_STATE_EXECUTIONCONTEXT_IS_NULL)
                {
                    return null;
                }
                else
                {
                    var props = m_contingentProperties;
                    if (props != null && props.m_capturedContext != null) return props.m_capturedContext;
                    else return ExecutionContext.PreAllocatedDefault;
                }
            }
            set
            {
                // There is no need to atomically set this bit because this set() method is only called during construction, and therefore there should be no contending accesses to m_stateFlags
                if (value == null)
                {
                    m_stateFlags |= TASK_STATE_EXECUTIONCONTEXT_IS_NULL;
                }
                else if (!value.IsPreAllocatedDefault) // not the default context, then inflate the contingent properties and set it
                {
                    EnsureContingentPropertiesInitialized(needsProtection: false).m_capturedContext = value;
                }
                //else do nothing, this is the default context
            }
        }

        /// <summary>
        /// Static helper function to copy specific ExecutionContext
        /// </summary>
        /// <param name="capturedContext">The captured context</param>
        /// <returns>The copied context, null if the capturedContext is null</returns>
        private static ExecutionContext CopyExecutionContext(ExecutionContext capturedContext)
        {
            if (capturedContext == null)
                return null;
            if (capturedContext.IsPreAllocatedDefault)
                return ExecutionContext.PreAllocatedDefault;

            return capturedContext.CreateCopy();
        }


#if DEBUG
        /// <summary>
        /// Retrieves an identifier for the task.
        /// </summary>
        internal int InternalId
        {
            get { return GetHashCode(); }
        }
#endif

        /////////////
        // methods


        /// <summary>
        /// Disposes the <see cref="Task"/>, releasing all of its unmanaged resources.  
        /// </summary>
        /// <remarks>
        /// Unlike most of the members of <see cref="Task"/>, this method is not thread-safe.
        /// Also, <see cref="Dispose()"/> may only be called on a <see cref="Task"/> that is in one of
        /// the final states: <see cref="System.Threading.Tasks.TaskStatus.RanToCompletion">RanToCompletion</see>,
        /// <see cref="System.Threading.Tasks.TaskStatus.Faulted">Faulted</see>, or
        /// <see cref="System.Threading.Tasks.TaskStatus.Canceled">Canceled</see>.
        /// </remarks>
        /// <exception cref="T:System.InvalidOperationException">
        /// The exception that is thrown if the <see cref="Task"/> is not in 
        /// one of the final states: <see cref="System.Threading.Tasks.TaskStatus.RanToCompletion">RanToCompletion</see>,
        /// <see cref="System.Threading.Tasks.TaskStatus.Faulted">Faulted</see>, or
        /// <see cref="System.Threading.Tasks.TaskStatus.Canceled">Canceled</see>.
        /// </exception>        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the <see cref="Task"/>, releasing all of its unmanaged resources.  
        /// </summary>
        /// <param name="disposing">
        /// A Boolean value that indicates whether this method is being called due to a call to <see
        /// cref="Dispose()"/>.
        /// </param>
        /// <remarks>
        /// Unlike most of the members of <see cref="Task"/>, this method is not thread-safe.
        /// </remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose is a nop if this task was created with the DoNotDispose internal option.
                // This is done before the completed check, because if we're not touching any 
                // state on the task, it's ok for it to happen before completion.
                if ((Options & (TaskCreationOptions)InternalTaskOptions.DoNotDispose) != 0)
                {
                    return;
                }

                // Task must be completed to dispose
                if (!IsCompleted)
                {
                    throw new InvalidOperationException(Environment.GetResourceString("Task_Dispose_NotCompleted"));
                }

                // Dispose of the underlying completion event if it exists
                var cp = m_contingentProperties;
                if (cp != null)
                {
                    // Make a copy to protect against racing Disposes.
                    // If we wanted to make this a bit safer, we could use an interlocked here,
                    // but we state that Dispose is not thread safe.
                    var ev = cp.m_completionEvent;
                    if (ev != null)
                    {
                        // Null out the completion event in contingent props; we'll use our copy from here on out
                        cp.m_completionEvent = null;

                        // In the unlikely event that our completion event is inflated but not yet signaled,
                        // go ahead and signal the event.  If you dispose of an unsignaled MRES, then any waiters
                        // will deadlock; an ensuing Set() will not wake them up.  In the event of an AppDomainUnload,
                        // there is no guarantee that anyone else is going to signal the event, and it does no harm to 
                        // call Set() twice on m_completionEvent.
                        if (!ev.IsSet) ev.Set();

                        // Finally, dispose of the event
                        ev.Dispose();
                    }
                }
            }

            // We OR the flags to indicate the object has been disposed. The task
            // has already completed at this point, and the only conceivable race condition would
            // be with the unsetting of the TASK_STATE_WAIT_COMPLETION_NOTIFICATION flag, which
            // is extremely unlikely and also benign.  (Worst case: we hit a breakpoint
            // twice instead of once in the debugger.  Weird, but not lethal.)
            m_stateFlags |= TASK_STATE_DISPOSED;
        }

        /////////////
        // internal helpers


        /// <summary>
        /// Schedules the task for execution.
        /// </summary>
        /// <param name="needsProtection">If true, TASK_STATE_STARTED bit is turned on in
        /// an atomic fashion, making sure that TASK_STATE_CANCELED does not get set
        /// underneath us.  If false, TASK_STATE_STARTED bit is OR-ed right in.  This
        /// allows us to streamline things a bit for StartNew(), where competing cancellations
        /// are not a problem.</param>
        [SecuritySafeCritical] // Needed for QueueTask
        internal void ScheduleAndStart(bool needsProtection)
        {
            Contract.Assert(m_taskScheduler != null, "expected a task scheduler to have been selected");
            Contract.Assert((m_stateFlags & TASK_STATE_STARTED) == 0, "task has already started");

            // Set the TASK_STATE_STARTED bit
            if (needsProtection)
            {
                if (!MarkStarted())
                {
                    // A cancel has snuck in before we could get started.  Quietly exit.
                    return;
                }
            }
            else
            {
                m_stateFlags |= TASK_STATE_STARTED;
            }

            if (s_asyncDebuggingEnabled)
            {
                AddToActiveTasks(this);
            }

            if (AsyncCausalityTracer.LoggingOn && (Options & (TaskCreationOptions)InternalTaskOptions.ContinuationTask) == 0)
            {
                //For all other task than TaskContinuations we want to log. TaskContinuations log in their constructor
                AsyncCausalityTracer.TraceOperationCreation(CausalityTraceLevel.Required, this.Id, "Task: "+((Delegate)m_action).Method.Name, 0);
            }


            try
            {
                // Queue to the indicated scheduler.
                m_taskScheduler.InternalQueueTask(this);
            }
            catch (ThreadAbortException tae)
            {
                AddException(tae);
                FinishThreadAbortedTask(true, false);
            }
            catch (Exception e)
            {
                // The scheduler had a problem queueing this task.  Record the exception, leaving this task in
                // a Faulted state.
                TaskSchedulerException tse = new TaskSchedulerException(e);
                AddException(tse);
                Finish(false);

                // Now we need to mark ourselves as "handled" to avoid crashing the finalizer thread if we are called from StartNew()
                // or from the self replicating logic, because in both cases the exception is either propagated outside directly, or added
                // to an enclosing parent. However we won't do this for continuation tasks, because in that case we internally eat the exception
                // and therefore we need to make sure the user does later observe it explicitly or see it on the finalizer.

                if ((Options & (TaskCreationOptions)InternalTaskOptions.ContinuationTask) == 0)
                {
                    // m_contingentProperties.m_exceptionsHolder *should* already exist after AddException()
                    Contract.Assert(
                        (m_contingentProperties != null) &&
                        (m_contingentProperties.m_exceptionsHolder != null) &&
                        (m_contingentProperties.m_exceptionsHolder.ContainsFaultList),
                            "Task.ScheduleAndStart(): Expected m_contingentProperties.m_exceptionsHolder to exist " +
                            "and to have faults recorded.");

                    m_contingentProperties.m_exceptionsHolder.MarkAsHandled(false);
                }
                // re-throw the exception wrapped as a TaskSchedulerException.
                throw tse;
            }
        }

        /// <summary>
        /// Adds an exception to the list of exceptions this task has thrown.
        /// </summary>
        /// <param name="exceptionObject">An object representing either an Exception or a collection of Exceptions.</param>
        internal void AddException(object exceptionObject)
        {
            Contract.Requires(exceptionObject != null, "Task.AddException: Expected a non-null exception object");
            AddException(exceptionObject, representsCancellation: false);
        }

        /// <summary>
        /// Adds an exception to the list of exceptions this task has thrown.
        /// </summary>
        /// <param name="exceptionObject">An object representing either an Exception or a collection of Exceptions.</param>
        /// <param name="representsCancellation">Whether the exceptionObject is an OperationCanceledException representing cancellation.</param>
        internal void AddException(object exceptionObject, bool representsCancellation)
        {
            Contract.Requires(exceptionObject != null, "Task.AddException: Expected a non-null exception object");

#if DEBUG
            var eoAsException = exceptionObject as Exception;
            var eoAsEnumerableException = exceptionObject as IEnumerable<Exception>;
            var eoAsEdi = exceptionObject as ExceptionDispatchInfo;
            var eoAsEnumerableEdi = exceptionObject as IEnumerable<ExceptionDispatchInfo>;

            Contract.Assert(
                eoAsException != null || eoAsEnumerableException != null || eoAsEdi != null || eoAsEnumerableEdi != null,
                "Task.AddException: Expected an Exception, ExceptionDispatchInfo, or an IEnumerable<> of one of those");

            var eoAsOce = exceptionObject as OperationCanceledException;

            Contract.Assert(
                !representsCancellation ||
                eoAsOce != null ||
                (eoAsEdi != null && eoAsEdi.SourceException is OperationCanceledException),
                "representsCancellation should be true only if an OCE was provided.");
#endif

            //
            // WARNING: A great deal of care went into ensuring that
            // AddException() and GetExceptions() are never called
            // simultaneously.  See comment at start of GetExceptions().
            //

            // Lazily initialize the holder, ensuring only one thread wins.
            var props = EnsureContingentPropertiesInitialized(needsProtection: true);
            if (props.m_exceptionsHolder == null)
            {
                TaskExceptionHolder holder = new TaskExceptionHolder(this);
                if (Interlocked.CompareExchange(ref props.m_exceptionsHolder, holder, null) != null)
                {
                    // If someone else already set the value, suppress finalization.
                    holder.MarkAsHandled(false);
                }
            }

            lock (props)
            {
                props.m_exceptionsHolder.Add(exceptionObject, representsCancellation);
            }
        }

        /// <summary>
        /// Returns a list of exceptions by aggregating the holder's contents. Or null if
        /// no exceptions have been thrown.
        /// </summary>
        /// <param name="includeTaskCanceledExceptions">Whether to include a TCE if cancelled.</param>
        /// <returns>An aggregate exception, or null if no exceptions have been caught.</returns>
        private AggregateException GetExceptions(bool includeTaskCanceledExceptions)
        {
            //
            // WARNING: The Task/Task<TResult>/TaskCompletionSource classes
            // have all been carefully crafted to insure that GetExceptions()
            // is never called while AddException() is being called.  There
            // are locks taken on m_contingentProperties in several places:
            //
            // -- Task<TResult>.TrySetException(): The lock allows the
            //    task to be set to Faulted state, and all exceptions to
            //    be recorded, in one atomic action.  
            //
            // -- Task.Exception_get(): The lock ensures that Task<TResult>.TrySetException()
            //    is allowed to complete its operation before Task.Exception_get()
            //    can access GetExceptions().
            //
            // -- Task.ThrowIfExceptional(): The lock insures that Wait() will
            //    not attempt to call GetExceptions() while Task<TResult>.TrySetException()
            //    is in the process of calling AddException().
            //
            // For "regular" tasks, we effectively keep AddException() and GetException()
            // from being called concurrently by the way that the state flows.  Until
            // a Task is marked Faulted, Task.Exception_get() returns null.  And
            // a Task is not marked Faulted until it and all of its children have
            // completed, which means that all exceptions have been recorded.
            //
            // It might be a lot easier to follow all of this if we just required
            // that all calls to GetExceptions() and AddExceptions() were made
            // under a lock on m_contingentProperties.  But that would also
            // increase our lock occupancy time and the frequency with which we
            // would need to take the lock.
            //
            // If you add a call to GetExceptions() anywhere in the code,
            // please continue to maintain the invariant that it can't be
            // called when AddException() is being called.
            //

            // We'll lazily create a TCE if the task has been canceled.
            Exception canceledException = null;
            if (includeTaskCanceledExceptions && IsCanceled)
            {
                // Backcompat: 
                // Ideally we'd just use the cached OCE from this.GetCancellationExceptionDispatchInfo()
                // here.  However, that would result in a potentially breaking change from .NET 4, which
                // has the code here that throws a new exception instead of the original, and the EDI
                // may not contain a TCE, but an OCE or any OCE-derived type, which would mean we'd be
                // propagating an exception of a different type.
                canceledException = new TaskCanceledException(this);
            }

            if (ExceptionRecorded)
            {
                // There are exceptions; get the aggregate and optionally add the canceled
                // exception to the aggregate (if applicable).
                Contract.Assert(m_contingentProperties != null); // ExceptionRecorded ==> m_contingentProperties != null

                // No need to lock around this, as other logic prevents the consumption of exceptions
                // before they have been completely processed.
                return m_contingentProperties.m_exceptionsHolder.CreateExceptionObject(false, canceledException);
            }
            else if (canceledException != null)
            {
                // No exceptions, but there was a cancelation. Aggregate and return it.
                return new AggregateException(canceledException);
            }

            return null;
        }

        /// <summary>Gets the exception dispatch infos once the task has faulted.</summary>
        internal ReadOnlyCollection<ExceptionDispatchInfo> GetExceptionDispatchInfos()
        {
            bool exceptionsAvailable = IsFaulted && ExceptionRecorded;
            Contract.Assert(exceptionsAvailable, "Must only be used when the task has faulted with exceptions.");
            return exceptionsAvailable ?
                m_contingentProperties.m_exceptionsHolder.GetExceptionDispatchInfos() :
                new ReadOnlyCollection<ExceptionDispatchInfo>(new ExceptionDispatchInfo[0]);
        }

        /// <summary>Gets the ExceptionDispatchInfo containing the OperationCanceledException for this task.</summary>
        /// <returns>The ExceptionDispatchInfo.  May be null if no OCE was stored for the task.</returns>
        internal ExceptionDispatchInfo GetCancellationExceptionDispatchInfo()
        {
            Contract.Assert(IsCanceled, "Must only be used when the task has canceled.");
            var props = m_contingentProperties;
            if (props == null) return null;
            var holder = props.m_exceptionsHolder;
            if (holder == null) return null;
            return holder.GetCancellationExceptionDispatchInfo(); // may be null
        }

        /// <summary>
        /// Throws an aggregate exception if the task contains exceptions. 
        /// </summary>
        internal void ThrowIfExceptional(bool includeTaskCanceledExceptions)
        {
            Contract.Requires(IsCompleted, "ThrowIfExceptional(): Expected IsCompleted == true");

            Exception exception = GetExceptions(includeTaskCanceledExceptions);
            if (exception != null)
            {
                UpdateExceptionObservedStatus();
                throw exception;
            }
        }

        /// <summary>
        /// Checks whether this is an attached task, and whether we are being called by the parent task.
        /// And sets the TASK_STATE_EXCEPTIONOBSERVEDBYPARENT status flag based on that.
        /// 
        /// This is meant to be used internally when throwing an exception, and when WaitAll is gathering 
        /// exceptions for tasks it waited on. If this flag gets set, the implicit wait on children 
        /// will skip exceptions to prevent duplication.
        /// 
        /// This should only be called when this task has completed with an exception
        /// 
        /// </summary>
        internal void UpdateExceptionObservedStatus()
        {
            if ((m_parent != null)
                && ((Options & TaskCreationOptions.AttachedToParent) != 0)
                && ((m_parent.CreationOptions & TaskCreationOptions.DenyChildAttach) == 0)
                && Task.InternalCurrent == m_parent)
            {
                m_stateFlags |= TASK_STATE_EXCEPTIONOBSERVEDBYPARENT;
            }
        }

        /// <summary>
        /// Checks whether the TASK_STATE_EXCEPTIONOBSERVEDBYPARENT status flag is set,
        /// This will only be used by the implicit wait to prevent double throws
        /// 
        /// </summary>
        internal bool IsExceptionObservedByParent
        {
            get
            {
                return (m_stateFlags & TASK_STATE_EXCEPTIONOBSERVEDBYPARENT) != 0;
            }
        }

        /// <summary>
        /// Checks whether the body was ever invoked. Used by task scheduler code to verify custom schedulers actually ran the task.
        /// </summary>
        internal bool IsDelegateInvoked
        {
            get
            {
                return (m_stateFlags & TASK_STATE_DELEGATE_INVOKED) != 0;
            }
        }

        /// <summary>
        /// Signals completion of this particular task.
        ///
        /// The bUserDelegateExecuted parameter indicates whether this Finish() call comes following the
        /// full execution of the user delegate. 
        /// 
        /// If bUserDelegateExecuted is false, it mean user delegate wasn't invoked at all (either due to
        /// a cancellation request, or because this task is a promise style Task). In this case, the steps
        /// involving child tasks (i.e. WaitForChildren) will be skipped.
        /// 
        /// </summary>
        internal void Finish(bool bUserDelegateExecuted)
        {
            if (!bUserDelegateExecuted)
            {
                // delegate didn't execute => no children. We can safely call the remaining finish stages
                FinishStageTwo();
            }
            else
            {
                var props = m_contingentProperties;

                if (props == null || // no contingent properties means no children, so it's safe to complete ourselves
                    (props.m_completionCountdown == 1 && !IsSelfReplicatingRoot) ||
                    // Count of 1 => either all children finished, or there were none. Safe to complete ourselves 
                    // without paying the price of an Interlocked.Decrement.
                    // However we need to exclude self replicating root tasks from this optimization, because
                    // they can have children joining in, or finishing even after the root task delegate is done.
                    Interlocked.Decrement(ref props.m_completionCountdown) == 0) // Reaching this sub clause means there may be remaining active children,
                // and we could be racing with one of them to call FinishStageTwo().
                // So whoever does the final Interlocked.Dec is responsible to finish.
                {
                    FinishStageTwo();
                }
                else
                {
                    // Apparently some children still remain. It will be up to the last one to process the completion of this task on their own thread.
                    // We will now yield the thread back to ThreadPool. Mark our state appropriately before getting out.

                    // We have to use an atomic update for this and make sure not to overwrite a final state, 
                    // because at this very moment the last child's thread may be concurrently completing us.
                    // Otherwise we risk overwriting the TASK_STATE_RAN_TO_COMPLETION, _CANCELED or _FAULTED bit which may have been set by that child task.
                    // Note that the concurrent update by the last child happening in FinishStageTwo could still wipe out the TASK_STATE_WAITING_ON_CHILDREN flag, 
                    // but it is not critical to maintain, therefore we dont' need to intruduce a full atomic update into FinishStageTwo

                    AtomicStateUpdate(TASK_STATE_WAITING_ON_CHILDREN, TASK_STATE_FAULTED | TASK_STATE_CANCELED | TASK_STATE_RAN_TO_COMPLETION);
                }

                // Now is the time to prune exceptional children. We'll walk the list and removes the ones whose exceptions we might have observed after they threw.
                // we use a local variable for exceptional children here because some other thread may be nulling out m_contingentProperties.m_exceptionalChildren 
                List<Task> exceptionalChildren = props != null ? props.m_exceptionalChildren : null;

                if (exceptionalChildren != null)
                {
                    lock (exceptionalChildren)
                    {
                        exceptionalChildren.RemoveAll(s_IsExceptionObservedByParentPredicate); // RemoveAll has better performance than doing it ourselves
                    }
                }
            }
        }

        // statically allocated delegate for the removeall expression in Finish()
        private readonly static Predicate<Task> s_IsExceptionObservedByParentPredicate = new Predicate<Task>((t) => { return t.IsExceptionObservedByParent; });

        /// <summary>
        /// FinishStageTwo is to be executed as soon as we known there are no more children to complete. 
        /// It can happen i) either on the thread that originally executed this task (if no children were spawned, or they all completed by the time this task's delegate quit)
        ///              ii) or on the thread that executed the last child.
        /// </summary>
        internal void FinishStageTwo()
        {
            AddExceptionsFromChildren();

            // At this point, the task is done executing and waiting for its children,
            // we can transition our task to a completion state.  
            int completionState;
            if (ExceptionRecorded)
            {
                completionState = TASK_STATE_FAULTED;
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Error);

                if (Task.s_asyncDebuggingEnabled)
                {
                    RemoveFromActiveTasks(this.Id);
                }
            }
            else if (IsCancellationRequested && IsCancellationAcknowledged)
            {
                // We transition into the TASK_STATE_CANCELED final state if the task's CT was signalled for cancellation, 
                // and the user delegate acknowledged the cancellation request by throwing an OCE, 
                // and the task hasn't otherwise transitioned into faulted state. (TASK_STATE_FAULTED trumps TASK_STATE_CANCELED)
                //
                // If the task threw an OCE without cancellation being requestsed (while the CT not being in signaled state),
                // then we regard it as a regular exception

                completionState = TASK_STATE_CANCELED;
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Canceled);

                if (Task.s_asyncDebuggingEnabled)
                {
                    RemoveFromActiveTasks(this.Id);
                }
            }
            else
            {
                completionState = TASK_STATE_RAN_TO_COMPLETION;
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Completed);

                if (Task.s_asyncDebuggingEnabled)
                {
                    RemoveFromActiveTasks(this.Id);
                }
            }

            // Use Interlocked.Exchange() to effect a memory fence, preventing
            // any SetCompleted() (or later) instructions from sneak back before it.
            Interlocked.Exchange(ref m_stateFlags, m_stateFlags | completionState);

            // Set the completion event if it's been lazy allocated.
            // And if we made a cancellation registration, it's now unnecessary.
            var cp = m_contingentProperties;
            if (cp != null)
            {
                cp.SetCompleted();
                cp.DeregisterCancellationCallback();
            }

            // ready to run continuations and notify parent.
            FinishStageThree();
        }


        /// <summary>
        /// Final stage of the task completion code path. Notifies the parent (if any) that another of its children are done, and runs continuations.
        /// This function is only separated out from FinishStageTwo because these two operations are also needed to be called from CancellationCleanupLogic()
        /// </summary>
        internal void FinishStageThree()
        {
            // Release the action so that holding this task object alive doesn't also
            // hold alive the body of the task.  We do this before notifying a parent,
            // so that if notifying the parent completes the parent and causes
            // its synchronous continuations to run, the GC can collect the state
            // in the interim.  And we do it before finishing continuations, because
            // continuations hold onto the task, and therefore are keeping it alive.
            m_action = null;

            // Notify parent if this was an attached task
            if (m_parent != null
                 && ((m_parent.CreationOptions & TaskCreationOptions.DenyChildAttach) == 0)
                 && (((TaskCreationOptions)(m_stateFlags & OptionsMask)) & TaskCreationOptions.AttachedToParent) != 0)
            {
                m_parent.ProcessChildCompletion(this);
            }

            // Activate continuations (if any).
            FinishContinuations();
        }

        /// <summary>
        /// This is called by children of this task when they are completed.
        /// </summary>
        internal void ProcessChildCompletion(Task childTask)
        {
            Contract.Requires(childTask != null);
            Contract.Requires(childTask.IsCompleted, "ProcessChildCompletion was called for an uncompleted task");

            Contract.Assert(childTask.m_parent == this, "ProcessChildCompletion should only be called for a child of this task");

            var props = m_contingentProperties;

            // if the child threw and we haven't observed it we need to save it for future reference
            if (childTask.IsFaulted && !childTask.IsExceptionObservedByParent)
            {
                // Lazily initialize the child exception list
                if (props.m_exceptionalChildren == null)
                {
                    Interlocked.CompareExchange(ref props.m_exceptionalChildren, new List<Task>(), null);
                }

                // In rare situations involving AppDomainUnload, it's possible (though unlikely) for FinishStageTwo() to be called
                // multiple times for the same task.  In that case, AddExceptionsFromChildren() could be nulling m_exceptionalChildren
                // out at the same time that we're processing it, resulting in a NullReferenceException here.  We'll protect
                // ourselves by caching m_exceptionChildren in a local variable.
                List<Task> tmp = props.m_exceptionalChildren;
                if (tmp != null)
                {
                    lock (tmp)
                    {
                        tmp.Add(childTask);
                    }
                }

            }

            if (Interlocked.Decrement(ref props.m_completionCountdown) == 0)
            {
                // This call came from the final child to complete, and apparently we have previously given up this task's right to complete itself.
                // So we need to invoke the final finish stage.

                FinishStageTwo();
            }
        }

        /// <summary>
        /// This is to be called just before the task does its final state transition. 
        /// It traverses the list of exceptional children, and appends their aggregate exceptions into this one's exception list
        /// </summary>
        internal void AddExceptionsFromChildren()
        {
            // In rare occurences during AppDomainUnload() processing, it is possible for this method to be called
            // simultaneously on the same task from two different contexts.  This can result in m_exceptionalChildren
            // being nulled out while it is being processed, which could lead to a NullReferenceException.  To
            // protect ourselves, we'll cache m_exceptionalChildren in a local variable.
            var props = m_contingentProperties;
            List<Task> tmp = (props != null) ? props.m_exceptionalChildren : null;

            if (tmp != null)
            {
                // This lock is necessary because even though AddExceptionsFromChildren is last to execute, it may still 
                // be racing with the code segment at the bottom of Finish() that prunes the exceptional child array. 
                lock (tmp)
                {
                    foreach (Task task in tmp)
                    {
                        // Ensure any exceptions thrown by children are added to the parent.
                        // In doing this, we are implicitly marking children as being "handled".
                        Contract.Assert(task.IsCompleted, "Expected all tasks in list to be completed");
                        if (task.IsFaulted && !task.IsExceptionObservedByParent)
                        {
                            TaskExceptionHolder exceptionHolder = task.m_contingentProperties.m_exceptionsHolder;
                            Contract.Assert(exceptionHolder != null);

                            // No locking necessary since child task is finished adding exceptions
                            // and concurrent CreateExceptionObject() calls do not constitute
                            // a concurrency hazard.
                            AddException(exceptionHolder.CreateExceptionObject(false, null));
                        }
                    }
                }

                // Reduce memory pressure by getting rid of the array
                props.m_exceptionalChildren = null;
            }
        }

        /// <summary>
        /// Special purpose Finish() entry point to be used when the task delegate throws a ThreadAbortedException
        /// This makes a note in the state flags so that we avoid any costly synchronous operations in the finish codepath
        /// such as inlined continuations
        /// </summary>
        /// <param name="bTAEAddedToExceptionHolder">
        /// Indicates whether the ThreadAbortException was added to this task's exception holder. 
        /// This should always be true except for the case of non-root self replicating task copies.
        /// </param>
        /// <param name="delegateRan">Whether the delegate was executed.</param>
        internal void FinishThreadAbortedTask(bool bTAEAddedToExceptionHolder, bool delegateRan)
        {
            Contract.Assert(!bTAEAddedToExceptionHolder || (m_contingentProperties != null && m_contingentProperties.m_exceptionsHolder != null),
                            "FinishThreadAbortedTask() called on a task whose exception holder wasn't initialized");

            // this will only be false for non-root self replicating task copies, because all of their exceptions go to the root task.
            if (bTAEAddedToExceptionHolder)
                m_contingentProperties.m_exceptionsHolder.MarkAsHandled(false);

            // If this method has already been called for this task, or if this task has already completed, then
            // return before actually calling Finish().
            if (!AtomicStateUpdate(TASK_STATE_THREAD_WAS_ABORTED,
                            TASK_STATE_THREAD_WAS_ABORTED | TASK_STATE_RAN_TO_COMPLETION | TASK_STATE_FAULTED | TASK_STATE_CANCELED))
            {
                return;
            }

            Finish(delegateRan);

        }


        /// <summary>
        /// Executes the task. This method will only be called once, and handles bookeeping associated with
        /// self-replicating tasks, in addition to performing necessary exception marshaling.
        /// </summary>
        private void Execute()
        {
            if (IsSelfReplicatingRoot)
            {
                ExecuteSelfReplicating(this);
            }
            else
            {
                try
                {
                    InnerInvoke();
                }
                catch (ThreadAbortException tae)
                {
                    // Don't record the TAE or call FinishThreadAbortedTask for a child replica task --
                    // it's already been done downstream.
                    if (!IsChildReplica)
                    {
                        // Record this exception in the task's exception list
                        HandleException(tae);

                        // This is a ThreadAbortException and it will be rethrown from this catch clause, causing us to 
                        // skip the regular Finish codepath. In order not to leave the task unfinished, we now call 
                        // FinishThreadAbortedTask here.
                        FinishThreadAbortedTask(true, true);
                    }
                }
                catch (Exception exn)
                {
                    // Record this exception in the task's exception list
                    HandleException(exn);
                }
            }
        }

        // Allows (internal) deriving classes to support limited replication.
        // (By default, replication is basically unlimited).
        internal virtual bool ShouldReplicate()
        {
            return true;
        }

        // Allows (internal) deriving classes to instantiate the task replica as a Task super class of their choice
        // (By default, we create a regular Task instance)
        internal virtual Task CreateReplicaTask(Action<object> taskReplicaDelegate, Object stateObject, Task parentTask, TaskScheduler taskScheduler,
                                            TaskCreationOptions creationOptionsForReplica, InternalTaskOptions internalOptionsForReplica)
        {
            return new Task(taskReplicaDelegate, stateObject, parentTask, default(CancellationToken),
                            creationOptionsForReplica, internalOptionsForReplica, parentTask.ExecutingTaskScheduler);
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to pass on state to the next replica
        internal virtual Object SavedStateForNextReplica
        {
            get { return null; }

            set { /*do nothing*/ }
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to pass on state to the next replica
        internal virtual Object SavedStateFromPreviousReplica
        {
            get { return null; }

            set { /*do nothing*/ }
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to hand over the child replica that they
        // had queued, so that the replacement replica can work with that child task instead of queuing up yet another one
        internal virtual Task HandedOverChildReplica
        {
            get { return null; }

            set { /* do nothing*/ }
        }

        private static void ExecuteSelfReplicating(Task root)
        {
            TaskCreationOptions creationOptionsForReplicas = root.CreationOptions | TaskCreationOptions.AttachedToParent;
            InternalTaskOptions internalOptionsForReplicas =
                InternalTaskOptions.ChildReplica |  // child replica flag disables self replication for the replicas themselves.
                InternalTaskOptions.SelfReplicating |  // we still want to identify this as part of a self replicating group
                InternalTaskOptions.QueuedByRuntime;   // we queue and cancel these tasks internally, so don't allow CT registration to take place


            // Important Note: The child replicas we launch from here will be attached the root replica (by virtue of the root.CreateReplicaTask call)
            // because we need the root task to receive all their exceptions, and to block until all of them return


            // This variable is captured in a closure and shared among all replicas.
            bool replicasAreQuitting = false;

            // Set up a delegate that will form the body of the root and all recursively created replicas.
            Action<object> taskReplicaDelegate = null;
            taskReplicaDelegate = delegate
            {
                Task currentTask = Task.InternalCurrent;


                // Check if a child task has been handed over by a prematurely quiting replica that we might be a replacement for.
                Task childTask = currentTask.HandedOverChildReplica;

                if (childTask == null)
                {
                    // Apparently we are not a replacement task. This means we need to queue up a child task for replication to progress

                    // Down-counts a counter in the root task.
                    if (!root.ShouldReplicate()) return;

                    // If any of the replicas have quit, we will do so ourselves.
                    if (Volatile.Read(ref replicasAreQuitting))
                    {
                        return;
                    }

                    // Propagate a copy of the context from the root task. It may be null if flow was suppressed.
                    ExecutionContext creatorContext = root.CapturedContext;


                    childTask = root.CreateReplicaTask(taskReplicaDelegate, root.m_stateObject, root, root.ExecutingTaskScheduler,
                                                       creationOptionsForReplicas, internalOptionsForReplicas);

                    childTask.CapturedContext = CopyExecutionContext(creatorContext);

                    childTask.ScheduleAndStart(false);
                }



                // Finally invoke the meat of the task.
                // Note that we are directly calling root.InnerInvoke() even though we are currently be in the action delegate of a child replica 
                // This is because the actual work was passed down in that delegate, and the action delegate of the child replica simply contains this
                // replication control logic.
                try
                {
                    // passing in currentTask only so that the parallel debugger can find it
                    root.InnerInvokeWithArg(currentTask);
                }
                catch (Exception exn)
                {
                    // Record this exception in the root task's exception list
                    root.HandleException(exn);

                    if (exn is ThreadAbortException)
                    {
                        // If this is a ThreadAbortException it will escape this catch clause, causing us to skip the regular Finish codepath
                        // In order not to leave the task unfinished, we now call FinishThreadAbortedTask here
                        currentTask.FinishThreadAbortedTask(false, true);
                    }
                }

                Object savedState = currentTask.SavedStateForNextReplica;

                // check for premature exit
                if (savedState != null)
                {
                    // the replica decided to exit early
                    // we need to queue up a replacement, attach the saved state, and yield the thread right away

                    Task replacementReplica = root.CreateReplicaTask(taskReplicaDelegate, root.m_stateObject, root, root.ExecutingTaskScheduler,
                                                                    creationOptionsForReplicas, internalOptionsForReplicas);

                    // Propagate a copy of the context from the root task to the replacement task
                    ExecutionContext creatorContext = root.CapturedContext;
                    replacementReplica.CapturedContext = CopyExecutionContext(creatorContext);

                    replacementReplica.HandedOverChildReplica = childTask;
                    replacementReplica.SavedStateFromPreviousReplica = savedState;

                    replacementReplica.ScheduleAndStart(false);
                }
                else
                {
                    // The replica finished normally, which means it can't find more work to grab. 
                    // Time to mark replicas quitting

                    replicasAreQuitting = true;

                    // InternalCancel() could conceivably throw in the underlying scheduler's TryDequeue() method.
                    // If it does, then make sure that we record it.
                    try
                    {
                        childTask.InternalCancel(true);
                    }
                    catch (Exception e)
                    {
                        // Apparently TryDequeue threw an exception.  Before propagating that exception, InternalCancel should have
                        // attempted an atomic state transition and a call to CancellationCleanupLogic() on this task. So we know
                        // the task was properly cleaned up if it was possible. 
                        //
                        // Now all we need to do is to Record the exception in the root task.

                        root.HandleException(e);
                    }

                    // No specific action needed if the child could not be canceled
                    // because we attached it to the root task, which should therefore be receiving any exceptions from the child,
                    // and root.wait will not return before this child finishes anyway.

                }
            };

            //
            // Now we execute as the root task
            //
            taskReplicaDelegate(null);
        }

        /// <summary>
        /// IThreadPoolWorkItem override, which is the entry function for this task when the TP scheduler decides to run it.
        /// 
        /// </summary>
        [SecurityCritical]
        void IThreadPoolWorkItem.ExecuteWorkItem()
        {
            ExecuteEntry(false);
        }

        /// <summary>
        /// The ThreadPool calls this if a ThreadAbortException is thrown while trying to execute this workitem.  This may occur
        /// before Task would otherwise be able to observe it.  
        /// </summary>
        [SecurityCritical]
        void IThreadPoolWorkItem.MarkAborted(ThreadAbortException tae)
        {
            // If the task has marked itself as Completed, then it either a) already observed this exception (so we shouldn't handle it here)
            // or b) completed before the exception ocurred (in which case it shouldn't count against this Task).
            if (!IsCompleted)
            {
                HandleException(tae);
                FinishThreadAbortedTask(true, false);
            }
        }

        /// <summary>
        /// Outermost entry function to execute this task. Handles all aspects of executing a task on the caller thread.
        /// Currently this is called by IThreadPoolWorkItem.ExecuteWorkItem(), and TaskManager.TryExecuteInline. 
        /// 
        /// </summary>
        /// <param name="bPreventDoubleExecution"> Performs atomic updates to prevent double execution. Should only be set to true
        /// in codepaths servicing user provided TaskSchedulers. The ConcRT or ThreadPool schedulers don't need this. </param>
        [SecuritySafeCritical]
        internal bool ExecuteEntry(bool bPreventDoubleExecution)
        {
            if (bPreventDoubleExecution || ((Options & (TaskCreationOptions)InternalTaskOptions.SelfReplicating) != 0))
            {
                int previousState = 0;

                // Do atomic state transition from queued to invoked. If we observe a task that's already invoked,
                // we will return false so that TaskScheduler.ExecuteTask can throw an exception back to the custom scheduler.
                // However we don't want this exception to be throw if the task was already canceled, because it's a
                // legitimate scenario for custom schedulers to dequeue a task and mark it as canceled (example: throttling scheduler)
                if (!AtomicStateUpdate(TASK_STATE_DELEGATE_INVOKED,
                                       TASK_STATE_DELEGATE_INVOKED | TASK_STATE_COMPLETED_MASK,
                                       ref previousState) && (previousState & TASK_STATE_CANCELED) == 0)
                {
                    // This task has already been invoked.  Don't invoke it again.
                    return false;
                }
            }
            else
            {
                // Remember that we started running the task delegate.
                m_stateFlags |= TASK_STATE_DELEGATE_INVOKED;
            }

            if (!IsCancellationRequested && !IsCanceled)
            {
                ExecuteWithThreadLocal(ref t_currentTask);
            }
            else if (!IsCanceled)
            {
                int prevState = Interlocked.Exchange(ref m_stateFlags, m_stateFlags | TASK_STATE_CANCELED);
                if ((prevState & TASK_STATE_CANCELED) == 0)
                {
                    CancellationCleanupLogic();
                }
            }

            return true;
        }

        // A trick so we can refer to the TLS slot with a byref.
        [SecurityCritical]
        private void ExecuteWithThreadLocal(ref Task currentTaskSlot)
        {
            // Remember the current task so we can restore it after running, and then
            Task previousTask = currentTaskSlot;

            // ETW event for Task Started
            var etwLog = TplEtwProvider.Log;
            Guid savedActivityID = new Guid();
            bool etwIsEnabled = etwLog.IsEnabled();
            if (etwIsEnabled)
            {
                if (etwLog.TasksSetActivityIds)
                    EventSource.SetCurrentThreadActivityId(TplEtwProvider.CreateGuidForTaskID(this.Id), out savedActivityID);
                // previousTask holds the actual "current task" we want to report in the event
                if (previousTask != null)
                    etwLog.TaskStarted(previousTask.m_taskScheduler.Id, previousTask.Id, this.Id);
                else
                    etwLog.TaskStarted(TaskScheduler.Current.Id, 0, this.Id);
            }

            if (AsyncCausalityTracer.LoggingOn)
                AsyncCausalityTracer.TraceSynchronousWorkStart(CausalityTraceLevel.Required, this.Id, CausalitySynchronousWork.Execution);


            try
            {
                // place the current task into TLS.
                currentTaskSlot = this;

                ExecutionContext ec = CapturedContext;
                if (ec == null)
                {
                    // No context, just run the task directly.
                    Execute();
                }
                else
                {
                    if (IsSelfReplicatingRoot || IsChildReplica)
                    {
                        CapturedContext = CopyExecutionContext(ec);
                    }

                    // Run the task.  We need a simple shim that converts the
                    // object back into a Task object, so that we can Execute it.

                    // Lazily initialize the callback delegate; benign race condition
                    var callback = s_ecCallback;
                    if (callback == null) s_ecCallback = callback = new ContextCallback(ExecutionContextCallback);
                    ExecutionContext.Run(ec, callback, this, true);
                }

                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceSynchronousWorkCompletion(CausalityTraceLevel.Required, CausalitySynchronousWork.Execution);

                Finish(true);
            }
            finally
            {
                currentTaskSlot = previousTask;
                
                // ETW event for Task Completed
                if (etwIsEnabled)
                {
                    // previousTask holds the actual "current task" we want to report in the event
                    if (previousTask != null)
                        etwLog.TaskCompleted(previousTask.m_taskScheduler.Id, previousTask.Id, this.Id, IsFaulted);
                    else
                        etwLog.TaskCompleted(TaskScheduler.Current.Id, 0, this.Id, IsFaulted);

                    if (etwLog.TasksSetActivityIds)
                        EventSource.SetCurrentThreadActivityId(savedActivityID);
                }
            }
        }

        // Cached callback delegate that's lazily initialized due to ContextCallback being SecurityCritical
        [SecurityCritical]
        private static ContextCallback s_ecCallback;

        [SecurityCritical]
        private static void ExecutionContextCallback(object obj)
        {
            Task task = obj as Task;
            Contract.Assert(task != null, "expected a task object");
            task.Execute();
        }


        /// <summary>
        /// The actual code which invokes the body of the task. This can be overriden in derived types.
        /// </summary>
        internal virtual void InnerInvoke()
        {
            // Invoke the delegate
            Contract.Assert(m_action != null, "Null action in InnerInvoke()");
            var action = m_action as Action;
            if (action != null)
            {
                action();
                return;
            }
            var actionWithState = m_action as Action<object>;
            if (actionWithState != null)
            {
                actionWithState(m_stateObject);
                return;
            }
            Contract.Assert(false, "Invalid m_action in Task");
        }

        /// <summary>
        /// Alternate InnerInvoke prototype to be called from ExecuteSelfReplicating() so that
        /// the Parallel Debugger can discover the actual task being invoked. 
        /// Details: Here, InnerInvoke is actually being called on the rootTask object while we are actually executing the
        /// childTask. And the debugger needs to discover the childTask, so we pass that down as an argument.
        /// The NoOptimization and NoInlining flags ensure that the childTask pointer is retained, and that this
        /// function appears on the callstack.
        /// </summary>
        /// <param name="childTask"></param>
        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        internal void InnerInvokeWithArg(Task childTask)
        {
            InnerInvoke();
        }

        /// <summary>
        /// Performs whatever handling is necessary for an unhandled exception. Normally
        /// this just entails adding the exception to the holder object. 
        /// </summary>
        /// <param name="unhandledException">The exception that went unhandled.</param>
        private void HandleException(Exception unhandledException)
        {
            Contract.Requires(unhandledException != null);

            OperationCanceledException exceptionAsOce = unhandledException as OperationCanceledException;
            if (exceptionAsOce != null && IsCancellationRequested &&
                m_contingentProperties.m_cancellationToken == exceptionAsOce.CancellationToken)
            {
                // All conditions are satisfied for us to go into canceled state in Finish().
                // Mark the acknowledgement.  The exception is also stored to enable it to be
                // the exception propagated from an await.

                SetCancellationAcknowledged();
                AddException(exceptionAsOce, representsCancellation: true);
            }
            else
            {
                // Other exceptions, including any OCE from the task that doesn't match the tasks' own CT, 
                // or that gets thrown without the CT being set will be treated as an ordinary exception 
                // and added to the aggregate.

                AddException(unhandledException);
            }
        }

        #region Await Support
        /// <summary>Gets an awaiter used to await this <see cref="System.Threading.Tasks.Task"/>.</summary>
        /// <returns>An awaiter instance.</returns>
        /// <remarks>This method is intended for compiler user rather than use directly in code.</remarks>
        public TaskAwaiter GetAwaiter()
        {
            return new TaskAwaiter(this);
        }

        /// <summary>Configures an awaiter used to await this <see cref="System.Threading.Tasks.Task"/>.</summary>
        /// <param name="continueOnCapturedContext">
        /// true to attempt to marshal the continuation back to the original context captured; otherwise, false.
        /// </param>
        /// <returns>An object used to await this task.</returns>
        public ConfiguredTaskAwaitable ConfigureAwait(bool continueOnCapturedContext)
        {
            return new ConfiguredTaskAwaitable(this, continueOnCapturedContext);
        }

        /// <summary>
        /// Sets a continuation onto the <see cref="System.Threading.Tasks.Task"/>.
        /// The continuation is scheduled to run in the current synchronization context is one exists, 
        /// otherwise in the current task scheduler.
        /// </summary>
        /// <param name="continuationAction">The action to invoke when the <see cref="System.Threading.Tasks.Task"/> has completed.</param>
        /// <param name="continueOnCapturedContext">
        /// true to attempt to marshal the continuation back to the original context captured; otherwise, false.
        /// </param>
        /// <param name="flowExecutionContext">Whether to flow ExecutionContext across the await.</param>
        /// <param name="stackMark">A stack crawl mark tied to execution context.</param>
        /// <exception cref="System.InvalidOperationException">The awaiter was not properly initialized.</exception>
        [SecurityCritical]
        internal void SetContinuationForAwait(
            Action continuationAction, bool continueOnCapturedContext, bool flowExecutionContext, ref StackCrawlMark stackMark)
        {
            Contract.Requires(continuationAction != null);

            // Create the best AwaitTaskContinuation object given the request.
            // If this remains null by the end of the function, we can use the 
            // continuationAction directly without wrapping it.
            TaskContinuation tc = null;

            // If the user wants the continuation to run on the current "context" if there is one...
            if (continueOnCapturedContext)
            {
                // First try getting the current synchronization context.
                // If the current context is really just the base SynchronizationContext type, 
                // which is intended to be equivalent to not having a current SynchronizationContext at all, 
                // then ignore it.  This helps with performance by avoiding unnecessary posts and queueing
                // of work items, but more so it ensures that if code happens to publish the default context 
                // as current, it won't prevent usage of a current task scheduler if there is one.
                var syncCtx = SynchronizationContext.CurrentNoFlow;
                if (syncCtx != null && syncCtx.GetType() != typeof(SynchronizationContext))
                {
                    tc = new SynchronizationContextAwaitTaskContinuation(syncCtx, continuationAction, flowExecutionContext, ref stackMark);
                }
                else
                {
                    // If there was no SynchronizationContext, then try for the current scheduler.
                    // We only care about it if it's not the default.
                    var scheduler = TaskScheduler.InternalCurrent;
                    if (scheduler != null && scheduler != TaskScheduler.Default)
                    {
                        tc = new TaskSchedulerAwaitTaskContinuation(scheduler, continuationAction, flowExecutionContext, ref stackMark);
                    }
                }
            }

            if (tc == null && flowExecutionContext)
            {
                // We're targeting the default scheduler, so we can use the faster path
                // that assumes the default, and thus we don't need to store it.  If we're flowing
                // ExecutionContext, we need to capture it and wrap it in an AwaitTaskContinuation.
                // Otherwise, we're targeting the default scheduler and we don't need to flow ExecutionContext, so
                // we don't actually need a continuation object.  We can just store/queue the action itself.
                tc = new AwaitTaskContinuation(continuationAction, flowExecutionContext: true, stackMark: ref stackMark);
            }

            // Now register the continuation, and if we couldn't register it because the task is already completing,
            // process the continuation directly (in which case make sure we schedule the continuation
            // rather than inlining it, the latter of which could result in a rare but possible stack overflow).
            if (tc != null)
            {
                if (!AddTaskContinuation(tc, addBeforeOthers: false))
                    tc.Run(this, bCanInlineContinuationTask: false);
            }
            else
            {
                Contract.Assert(!flowExecutionContext, "We already determined we're not required to flow context.");
                if (!AddTaskContinuation(continuationAction, addBeforeOthers: false))
                    AwaitTaskContinuation.UnsafeScheduleAction(continuationAction, this);
            }
        }

        /// <summary>Creates an awaitable that asynchronously yields back to the current context when awaited.</summary>
        /// <returns>
        /// A context that, when awaited, will asynchronously transition back into the current context at the 
        /// time of the await. If the current SynchronizationContext is non-null, that is treated as the current context.
        /// Otherwise, TaskScheduler.Current is treated as the current context.
        /// </returns>
        public static YieldAwaitable Yield()
        {
            return new YieldAwaitable();
        }
        #endregion

        /// <summary>
        /// Waits for the <see cref="Task"/> to complete execution.
        /// </summary>
        /// <exception cref="T:System.AggregateException">
        /// The <see cref="Task"/> was canceled -or- an exception was thrown during
        /// the execution of the <see cref="Task"/>.
        /// </exception>
        public void Wait()
        {
#if DEBUG
            bool waitResult =
#endif
            Wait(Timeout.Infinite, default(CancellationToken));

#if DEBUG
            Contract.Assert(waitResult, "expected wait to succeed");
#endif
        }

        /// <summary>
        /// Waits for the <see cref="Task"/> to complete execution.
        /// </summary>
        /// <param name="timeout">
        /// A <see cref="System.TimeSpan"/> that represents the number of milliseconds to wait, or a <see
        /// cref="System.TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <returns>
        /// true if the <see cref="Task"/> completed execution within the allotted time; otherwise, false.
        /// </returns>
        /// <exception cref="T:System.AggregateException">
        /// The <see cref="Task"/> was canceled -or- an exception was thrown during the execution of the <see
        /// cref="Task"/>.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents an
        /// infinite time-out -or- timeout is greater than
        /// <see cref="System.Int32.MaxValue"/>.
        /// </exception>
        public bool Wait(TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }

            return Wait((int)totalMilliseconds, default(CancellationToken));
        }


        /// <summary>
        /// Waits for the <see cref="Task"/> to complete execution.
        /// </summary>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for the task to complete.
        /// </param>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// The <see cref="Task"/> was canceled -or- an exception was thrown during the execution of the <see
        /// cref="Task"/>.
        /// </exception>
        public void Wait(CancellationToken cancellationToken)
        {
            Wait(Timeout.Infinite, cancellationToken);
        }


        /// <summary>
        /// Waits for the <see cref="Task"/> to complete execution.
        /// </summary>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.</param>
        /// <returns>true if the <see cref="Task"/> completed execution within the allotted time; otherwise,
        /// false.
        /// </returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// The <see cref="Task"/> was canceled -or- an exception was thrown during the execution of the <see
        /// cref="Task"/>.
        /// </exception>
        public bool Wait(int millisecondsTimeout)
        {
            return Wait(millisecondsTimeout, default(CancellationToken));
        }


        /// <summary>
        /// Waits for the <see cref="Task"/> to complete execution.
        /// </summary>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for the task to complete.
        /// </param>
        /// <returns>
        /// true if the <see cref="Task"/> completed execution within the allotted time; otherwise, false.
        /// </returns>
        /// <exception cref="T:System.AggregateException">
        /// The <see cref="Task"/> was canceled -or- an exception was thrown during the execution of the <see
        /// cref="Task"/>.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException("millisecondsTimeout");
            }
            Contract.EndContractBlock();

            // Return immediately if we know that we've completed "clean" -- no exceptions, no cancellations
            // and if no notification to the debugger is required
            if (!IsWaitNotificationEnabledOrNotRanToCompletion) // (!DebuggerBitSet && RanToCompletion)
                return true;

            // Wait, and then return if we're still not done.
            if (!InternalWait(millisecondsTimeout, cancellationToken))
                return false;

            if (IsWaitNotificationEnabledOrNotRanToCompletion) // avoid a few unnecessary volatile reads if we completed successfully
            {
                // Notify the debugger of the wait completion if it's requested such a notification
                NotifyDebuggerOfWaitCompletionIfNecessary();

                // If cancellation was requested and the task was canceled, throw an 
                // OperationCanceledException.  This is prioritized ahead of the ThrowIfExceptional
                // call to bring more determinism to cases where the same token is used to 
                // cancel the Wait and to cancel the Task.  Otherwise, there's a race condition between
                // whether the Wait or the Task observes the cancellation request first,
                // and different exceptions result from the different cases.
                if (IsCanceled) cancellationToken.ThrowIfCancellationRequested();

                // If an exception occurred, or the task was cancelled, throw an exception.
                ThrowIfExceptional(true);
            }

            Contract.Assert((m_stateFlags & TASK_STATE_FAULTED) == 0, "Task.Wait() completing when in Faulted state.");

            return true;
        }

        // Convenience method that wraps any scheduler exception in a TaskSchedulerException
        // and rethrows it.
        private bool WrappedTryRunInline()
        {
            if (m_taskScheduler == null)
                return false;

            try
            {
                return m_taskScheduler.TryRunInline(this, true);
            }
            catch (Exception e)
            {
                // we 1) either received an unexpected exception originating from a custom scheduler, which needs to be wrapped in a TSE and thrown
                //    2) or a a ThreadAbortException, which we need to skip here, because it would already have been handled in Task.Execute
                if (!(e is ThreadAbortException))
                {
                    TaskSchedulerException tse = new TaskSchedulerException(e);
                    throw tse;
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// The core wait function, which is only accesible internally. It's meant to be used in places in TPL code where 
        /// the current context is known or cached.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        internal bool InternalWait(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            // ETW event for Task Wait Begin
            var etwLog = TplEtwProvider.Log;
            bool etwIsEnabled = etwLog.IsEnabled();
            if (etwIsEnabled)
            {
                Task currentTask = Task.InternalCurrent;
                etwLog.TaskWaitBegin(
                    (currentTask != null ? currentTask.m_taskScheduler.Id : TaskScheduler.Default.Id), (currentTask != null ? currentTask.Id : 0),
                    this.Id, TplEtwProvider.TaskWaitBehavior.Synchronous, 0, System.Threading.Thread.GetDomainID());
            }

            bool returnValue = IsCompleted;

            // If the event hasn't already been set, we will wait.
            if (!returnValue)
            {
                // Alert a listening debugger that we can't make forward progress unless it slips threads.
                // We call NOCTD for two reasons:
                //    1. If the task runs on another thread, then we'll be blocked here indefinitely.
                //    2. If the task runs inline but takes some time to complete, it will suffer ThreadAbort with possible state corruption,
                //       and it is best to prevent this unless the user explicitly asks to view the value with thread-slipping enabled.
                Debugger.NotifyOfCrossThreadDependency();

                // We will attempt inline execution only if an infinite wait was requested
                // Inline execution doesn't make sense for finite timeouts and if a cancellation token was specified
                // because we don't know how long the task delegate will take.
                if (millisecondsTimeout == Timeout.Infinite && !cancellationToken.CanBeCanceled &&
                    WrappedTryRunInline() && IsCompleted) // TryRunInline doesn't guarantee completion, as there may be unfinished children.
                {
                    returnValue = true;
                }
                else
                {
                    returnValue = SpinThenBlockingWait(millisecondsTimeout, cancellationToken);
                }
            }

            Contract.Assert(IsCompleted || millisecondsTimeout != Timeout.Infinite);

            // ETW event for Task Wait End
            if (etwIsEnabled)
            {
                Task currentTask = Task.InternalCurrent;
                if (currentTask != null)
                {
                    etwLog.TaskWaitEnd(currentTask.m_taskScheduler.Id, currentTask.Id, this.Id);
                }
                else
                {
                    etwLog.TaskWaitEnd(TaskScheduler.Default.Id, 0, this.Id);
                }
                // logically the continuation is empty so we immediately fire
                etwLog.TaskWaitContinuationComplete(this.Id);
            }

            return returnValue;
        }

        // An MRES that gets set when Invoke is called.  This replaces old logic that looked like this:
        //      ManualResetEventSlim mres = new ManualResetEventSlim(false, 0);
        //      Action<Task> completionAction = delegate {mres.Set();}
        //      AddCompletionAction(completionAction);
        // with this:
        //      SetOnInvokeMres mres = new SetOnInvokeMres();
        //      AddCompletionAction(mres, addBeforeOthers: true);
        // which saves a couple of allocations.
        //
        // Used in SpinThenBlockingWait (below), but could be seen as a general purpose mechanism.
        private sealed class SetOnInvokeMres : ManualResetEventSlim, ITaskCompletionAction
        {
            internal SetOnInvokeMres() : base(false, 0) { }
            public void Invoke(Task completingTask) { Set(); }
            public bool InvokeMayRunArbitraryCode { get { return false; } }
        }

        /// <summary>
        /// Waits for the task to complete, for a timeout to occur, or for cancellation to be requested.
        /// The method first spins and then falls back to blocking on a new event.
        /// </summary>
        /// <param name="millisecondsTimeout">The timeout.</param>
        /// <param name="cancellationToken">The token.</param>
        /// <returns>true if the task is completed; otherwise, false.</returns>
        private bool SpinThenBlockingWait(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            bool infiniteWait = millisecondsTimeout == Timeout.Infinite;
            uint startTimeTicks = infiniteWait ? 0 : (uint)Environment.TickCount;
            bool returnValue = SpinWait(millisecondsTimeout);
            if (!returnValue)
            {
                var mres = new SetOnInvokeMres();
                try
                {
                    AddCompletionAction(mres, addBeforeOthers: true);
                    if (infiniteWait)
                    {
                        returnValue = mres.Wait(Timeout.Infinite, cancellationToken);
                    }
                    else
                    {
                        uint elapsedTimeTicks = ((uint)Environment.TickCount) - startTimeTicks;
                        if (elapsedTimeTicks < millisecondsTimeout)
                        {
                            returnValue = mres.Wait((int)(millisecondsTimeout - elapsedTimeTicks), cancellationToken);
                        }
                    }
                }
                finally
                {
                    if (!IsCompleted) RemoveContinuation(mres);
                    // Don't Dispose of the MRES, because the continuation off of this task may
                    // still be running.  This is ok, however, as we never access the MRES' WaitHandle,
                    // and thus no finalizable resources are actually allocated.
                }
            }
            return returnValue;
        }

        /// <summary>
        /// Spins briefly while checking IsCompleted
        /// </summary>
        /// <param name="millisecondsTimeout">The timeout.</param>
        /// <returns>true if the task is completed; otherwise, false.</returns>
        /// <exception cref="System.OperationCanceledException">The wait was canceled.</exception>
        private bool SpinWait(int millisecondsTimeout)
        {
            if (IsCompleted) return true;

            if (millisecondsTimeout == 0)
            {
                // For 0-timeouts, we just return immediately.
                return false;
            }

            //This code is pretty similar to the custom spinning in MRES except there is no yieling after we exceed the spin count
            int spinCount = PlatformHelper.IsSingleProcessor ? 1 : System.Threading.SpinWait.YIELD_THRESHOLD; //spin only once if we are running on a single CPU
            for (int i = 0; i < spinCount; i++)
            {
                if (IsCompleted)
                {
                    return true;
                }

                if (i == spinCount / 2)
                {
                    Thread.Yield();
                }
                else
                {
                    Thread.SpinWait(PlatformHelper.ProcessorCount * (4 << i));
                }

            }

            return IsCompleted;
        }

        /// <summary>
        /// Cancels the <see cref="Task"/>.
        /// </summary>
        /// <param name="bCancelNonExecutingOnly"> 
        /// Indicates whether we should only cancel non-invoked tasks.
        /// For the default scheduler this option will only be serviced through TryDequeue.
        /// For custom schedulers we also attempt an atomic state transition.
        /// </param>
        /// <returns>true if the task was successfully canceled; otherwise, false.</returns>
        [SecuritySafeCritical]
        internal bool InternalCancel(bool bCancelNonExecutingOnly)
        {
            Contract.Requires((Options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) == 0, "Task.InternalCancel() did not expect promise-style task");

            bool bPopSucceeded = false;
            bool mustCleanup = false;

            TaskSchedulerException tse = null;

            // If started, and running in a task context, we can try to pop the chore.
            if ((m_stateFlags & TASK_STATE_STARTED) != 0)
            {
                TaskScheduler ts = m_taskScheduler;

                try
                {
                    bPopSucceeded = (ts != null) && ts.TryDequeue(this);
                }
                catch (Exception e)
                {
                    // TryDequeue threw. We don't know whether the task was properly dequeued or not. So we must let the rest of 
                    // the cancellation logic run its course (record the request, attempt atomic state transition and do cleanup where appropriate)
                    // Here we will only record a TaskSchedulerException, which will later be thrown at function exit.

                    if (!(e is ThreadAbortException))
                    {
                        tse = new TaskSchedulerException(e);
                    }
                }

                bool bRequiresAtomicStartTransition = (ts != null && ts.RequiresAtomicStartTransition) || ((Options & (TaskCreationOptions)InternalTaskOptions.SelfReplicating) != 0);

                if (!bPopSucceeded && bCancelNonExecutingOnly && bRequiresAtomicStartTransition)
                {
                    // The caller requested cancellation of non-invoked tasks only, and TryDequeue was one way of doing it...
                    // Since that seems to have failed, we should now try an atomic state transition (from non-invoked state to canceled)
                    // An atomic transition here is only safe if we know we're on a custom task scheduler, which also forces a CAS on ExecuteEntry

                    // Even though this task can't have any children, we should be ready for handling any continuations that 
                    // may be attached to it (although currently 
                    // So we need to remeber whether we actually did the flip, so we can do clean up (finish continuations etc)
                    mustCleanup = AtomicStateUpdate(TASK_STATE_CANCELED, TASK_STATE_DELEGATE_INVOKED | TASK_STATE_CANCELED);


                    // PS: This is slightly different from the regular cancellation codepath 
                    // since we record the cancellation request *after* doing the state transition. 
                    // However that shouldn't matter too much because the task was never invoked, thus can't have children
                }

            }

            if (!bCancelNonExecutingOnly || bPopSucceeded || mustCleanup)
            {
                // Record the cancellation request.
                RecordInternalCancellationRequest();

                // Determine whether we need to clean up
                // This will be the case 
                //     1) if we were able to pop, and we are able to update task state to TASK_STATE_CANCELED
                //     2) if the task seems to be yet unstarted, and we can transition to
                //        TASK_STATE_CANCELED before anyone else can transition into _STARTED or _CANCELED or 
                //        _RAN_TO_COMPLETION or _FAULTED
                // Note that we do not check for TASK_STATE_COMPLETION_RESERVED.  That only applies to promise-style
                // tasks, and a promise-style task should not enter into this codepath.
                if (bPopSucceeded)
                {
                    // hitting this would mean something wrong with the AtomicStateUpdate above
                    Contract.Assert(!mustCleanup, "Possibly an invalid state transition call was made in InternalCancel()");

                    // Include TASK_STATE_DELEGATE_INVOKED in "illegal" bits to protect against the situation where
                    // TS.TryDequeue() returns true but the task is still left on the queue.
                    mustCleanup = AtomicStateUpdate(TASK_STATE_CANCELED, TASK_STATE_CANCELED | TASK_STATE_DELEGATE_INVOKED);
                }
                else if (!mustCleanup && (m_stateFlags & TASK_STATE_STARTED) == 0)
                {
                    mustCleanup = AtomicStateUpdate(TASK_STATE_CANCELED,
                        TASK_STATE_CANCELED | TASK_STATE_STARTED | TASK_STATE_RAN_TO_COMPLETION |
                        TASK_STATE_FAULTED | TASK_STATE_DELEGATE_INVOKED);
                }

                // do the cleanup (i.e. set completion event and finish continuations)
                if (mustCleanup)
                {
                    CancellationCleanupLogic();
                }
            }

            if (tse != null)
                throw tse;
            else
                return (mustCleanup);
        }

        // Breaks out logic for recording a cancellation request
        internal void RecordInternalCancellationRequest()
        {
            // Record the cancellation request.
            var props = EnsureContingentPropertiesInitialized(needsProtection: true);
            props.m_internalCancellationRequested = CANCELLATION_REQUESTED;

        }

        // Breaks out logic for recording a cancellation request
        // This overload should only be used for promise tasks where no cancellation token
        // was supplied when the task was created.
        internal void RecordInternalCancellationRequest(CancellationToken tokenToRecord)
        {
            RecordInternalCancellationRequest();

            Contract.Assert((Options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) != 0, "Task.RecordInternalCancellationRequest(CancellationToken) only valid for promise-style task");
            Contract.Assert(m_contingentProperties.m_cancellationToken == default(CancellationToken));

            // Store the supplied cancellation token as this task's token.
            // Waiting on this task will then result in an OperationCanceledException containing this token.
            if (tokenToRecord != default(CancellationToken))
            {
                m_contingentProperties.m_cancellationToken = tokenToRecord;
            }
        }

        // Breaks out logic for recording a cancellation request
        // This overload should only be used for promise tasks where no cancellation token
        // was supplied when the task was created.
        internal void RecordInternalCancellationRequest(CancellationToken tokenToRecord, object cancellationException)
        {
            RecordInternalCancellationRequest(tokenToRecord);

            // Store the supplied cancellation exception
            if (cancellationException != null)
            {
#if DEBUG
                var oce = cancellationException as OperationCanceledException;
                if (oce == null)
                {
                    var edi = cancellationException as ExceptionDispatchInfo;
                    Contract.Assert(edi != null, "Expected either an OCE or an EDI");
                    oce = edi.SourceException as OperationCanceledException;
                    Contract.Assert(oce != null, "Expected EDI to contain an OCE");
                }
                Contract.Assert(oce.CancellationToken == tokenToRecord, 
                                "Expected OCE's token to match the provided token.");
#endif
                AddException(cancellationException, representsCancellation: true);
            }
        }

        // ASSUMES THAT A SUCCESSFUL CANCELLATION HAS JUST OCCURRED ON THIS TASK!!!
        // And this method should be called at most once per task.
        internal void CancellationCleanupLogic()
        {
            Contract.Assert((m_stateFlags & (TASK_STATE_CANCELED | TASK_STATE_COMPLETION_RESERVED)) != 0, "Task.CancellationCleanupLogic(): Task not canceled or reserved.");
            // I'd like to do this, but there is a small window for a race condition.  If someone calls Wait() between InternalCancel() and
            // here, that will set m_completionEvent, leading to a meaningless/harmless assertion.
            //Contract.Assert((m_completionEvent == null) || !m_completionEvent.IsSet, "Task.CancellationCleanupLogic(): Completion event already set.");

            // This may have been set already, but we need to make sure.
            Interlocked.Exchange(ref m_stateFlags, m_stateFlags | TASK_STATE_CANCELED);

            // Fire completion event if it has been lazily initialized
            var cp = m_contingentProperties;
            if (cp != null)
            {
                cp.SetCompleted();
                cp.DeregisterCancellationCallback();
            }

            if (AsyncCausalityTracer.LoggingOn)
                AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Canceled);

            if (Task.s_asyncDebuggingEnabled)
            {
                RemoveFromActiveTasks(this.Id);
            }

            // Notify parents, fire continuations, other cleanup.
            FinishStageThree();
        }


        /// <summary>
        /// Sets the task's cancellation acknowledged flag.
        /// </summary>    
        private void SetCancellationAcknowledged()
        {
            Contract.Assert(this == Task.InternalCurrent, "SetCancellationAcknowledged() should only be called while this is still the current task");
            Contract.Assert(IsCancellationRequested, "SetCancellationAcknowledged() should not be called if the task's CT wasn't signaled");

            m_stateFlags |= TASK_STATE_CANCELLATIONACKNOWLEDGED;
        }


        //
        // Continuation passing functionality (aka ContinueWith)
        //




        /// <summary>
        /// Runs all of the continuations, as appropriate.
        /// </summary>
        [SecuritySafeCritical] // for AwaitTaskContinuation.RunOrScheduleAction
        internal void FinishContinuations()
        {
            // Atomically store the fact that this task is completing.  From this point on, the adding of continuations will
            // result in the continuations being run/launched directly rather than being added to the continuation list.
            object continuationObject = Interlocked.Exchange(ref m_continuationObject, s_taskCompletionSentinel);
            TplEtwProvider.Log.RunningContinuation(Id, continuationObject);

            // If continuationObject == null, then we don't have any continuations to process
            if (continuationObject != null)
            {

                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceSynchronousWorkStart(CausalityTraceLevel.Required, this.Id, CausalitySynchronousWork.CompletionNotification);

                // Skip synchronous execution of continuations if this task's thread was aborted
                bool bCanInlineContinuations = !(((m_stateFlags & TASK_STATE_THREAD_WAS_ABORTED) != 0) ||
                                                  (Thread.CurrentThread.ThreadState == ThreadState.AbortRequested) ||
                                                  ((m_stateFlags & (int)TaskCreationOptions.RunContinuationsAsynchronously) != 0));

                // Handle the single-Action case
                Action singleAction = continuationObject as Action;
                if (singleAction != null)
                {
                    AwaitTaskContinuation.RunOrScheduleAction(singleAction, bCanInlineContinuations, ref t_currentTask);
                    LogFinishCompletionNotification();
                    return;
                }

                // Handle the single-ITaskCompletionAction case
                ITaskCompletionAction singleTaskCompletionAction = continuationObject as ITaskCompletionAction;
                if (singleTaskCompletionAction != null)
                {
                    if (bCanInlineContinuations || !singleTaskCompletionAction.InvokeMayRunArbitraryCode)
                    {
                        singleTaskCompletionAction.Invoke(this);
                    }
                    else
                    {
                        ThreadPool.UnsafeQueueCustomWorkItem(new CompletionActionInvoker(singleTaskCompletionAction, this), forceGlobal: false);
                    }
                    LogFinishCompletionNotification();
                    return;
                }

                // Handle the single-TaskContinuation case
                TaskContinuation singleTaskContinuation = continuationObject as TaskContinuation;
                if (singleTaskContinuation != null)
                {
                    singleTaskContinuation.Run(this, bCanInlineContinuations);
                    LogFinishCompletionNotification();
                    return;
                }

                // Not a single; attempt to cast as list
                List<object> continuations = continuationObject as List<object>;

                if (continuations == null)
                {
                    LogFinishCompletionNotification();
                    return;  // Not a single or a list; just return
                }

                //
                // Begin processing of continuation list
                //

                // Wait for any concurrent adds or removes to be retired
                lock (continuations) { }
                int continuationCount = continuations.Count;

                // Fire the asynchronous continuations first ...
                for (int i = 0; i < continuationCount; i++)
                {
                    // Synchronous continuation tasks will have the ExecuteSynchronously option,
                    // and we're looking for asynchronous tasks...
                    var tc = continuations[i] as StandardTaskContinuation;
                    if (tc != null && (tc.m_options & TaskContinuationOptions.ExecuteSynchronously) == 0)
                    {
                        TplEtwProvider.Log.RunningContinuationList(Id, i, tc);
                        continuations[i] = null; // so that we can skip this later
                        tc.Run(this, bCanInlineContinuations);
                    }
                }

                // ... and then fire the synchronous continuations (if there are any).
                // This includes ITaskCompletionAction, AwaitTaskContinuations, and
                // Action delegates, which are all by default implicitly synchronous.
                for (int i = 0; i < continuationCount; i++)
                {
                    object currentContinuation = continuations[i];
                    if (currentContinuation == null) continue;
                    continuations[i] = null; // to enable free'ing up memory earlier
                    TplEtwProvider.Log.RunningContinuationList(Id, i, currentContinuation);

                    // If the continuation is an Action delegate, it came from an await continuation,
                    // and we should use AwaitTaskContinuation to run it.
                    Action ad = currentContinuation as Action;
                    if (ad != null)
                    {
                        AwaitTaskContinuation.RunOrScheduleAction(ad, bCanInlineContinuations, ref t_currentTask);
                    }
                    else
                    {
                        // If it's a TaskContinuation object of some kind, invoke it.
                        TaskContinuation tc = currentContinuation as TaskContinuation;
                        if (tc != null)
                        {
                            // We know that this is a synchronous continuation because the
                            // asynchronous ones have been weeded out
                            tc.Run(this, bCanInlineContinuations);
                        }
                        // Otherwise, it must be an ITaskCompletionAction, so invoke it.
                        else
                        {
                            Contract.Assert(currentContinuation is ITaskCompletionAction, "Expected continuation element to be Action, TaskContinuation, or ITaskContinuationAction");
                            var action = (ITaskCompletionAction)currentContinuation;

                            if (bCanInlineContinuations || !action.InvokeMayRunArbitraryCode)
                            {
                                action.Invoke(this);
                            }
                            else
                            {
                                ThreadPool.UnsafeQueueCustomWorkItem(new CompletionActionInvoker(action, this), forceGlobal: false);
                            }
                        }
                    }
                }

                LogFinishCompletionNotification();
            }
        }

        private void LogFinishCompletionNotification()
        {
            if (AsyncCausalityTracer.LoggingOn)
                AsyncCausalityTracer.TraceSynchronousWorkCompletion(CausalityTraceLevel.Required, CausalitySynchronousWork.CompletionNotification);
        }

        #region Continuation methods

        #region Action<Task> continuation
        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task> continuationAction)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, TaskScheduler.Current, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="cancellationToken"> The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task> continuationAction, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, TaskScheduler.Current, cancellationToken, TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes.  When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task> continuationAction, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, scheduler, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed. If the continuation criteria specified through the <paramref
        /// name="continuationOptions"/> parameter are not met, the continuation task will be canceled
        /// instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task> continuationAction, TaskContinuationOptions continuationOptions)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, TaskScheduler.Current, default(CancellationToken), continuationOptions, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its
        /// execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed. If the criteria specified through the <paramref name="continuationOptions"/> parameter
        /// are not met, the continuation task will be canceled instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task> continuationAction, CancellationToken cancellationToken,
                                 TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, scheduler, cancellationToken, continuationOptions, ref stackMark);
        }

        // Same as the above overload, just with a stack mark parameter.
        private Task ContinueWith(Action<Task> continuationAction, TaskScheduler scheduler,
            CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, ref StackCrawlMark stackMark)
        {
            // Throw on continuation with null action
            if (continuationAction == null)
            {
                throw new ArgumentNullException("continuationAction");
            }

            // Throw on continuation with null TaskScheduler
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            TaskCreationOptions creationOptions;
            InternalTaskOptions internalOptions;
            CreationOptionsFromContinuationOptions(continuationOptions, out creationOptions, out internalOptions);

            Task continuationTask = new ContinuationTaskFromTask(
                this, continuationAction, null,
                creationOptions, internalOptions,
                ref stackMark
            );

            // Register the continuation.  If synchronous execution is requested, this may
            // actually invoke the continuation before returning.
            ContinueWithCore(continuationTask, scheduler, cancellationToken, continuationOptions);

            return continuationTask;
        }
        #endregion

        #region Action<Task, Object> continuation

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation action.</param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task, Object> continuationAction, Object state)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, state, TaskScheduler.Current, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation action.</param>
        /// <param name="cancellationToken"> The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task, Object> continuationAction, Object state, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, state, TaskScheduler.Current, cancellationToken, TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes.  When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation action.</param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task, Object> continuationAction, Object state, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, state, scheduler, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation action.</param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed. If the continuation criteria specified through the <paramref
        /// name="continuationOptions"/> parameter are not met, the continuation task will be canceled
        /// instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task ContinueWith(Action<Task, Object> continuationAction, Object state, TaskContinuationOptions continuationOptions)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, state, TaskScheduler.Current, default(CancellationToken), continuationOptions, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <param name="continuationAction">
        /// An action to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation action.</param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its
        /// execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task"/> will not be scheduled for execution until the current task has
        /// completed. If the criteria specified through the <paramref name="continuationOptions"/> parameter
        /// are not met, the continuation task will be canceled instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationAction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable
        public Task ContinueWith(Action<Task, Object> continuationAction, Object state, CancellationToken cancellationToken,
                                 TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith(continuationAction, state, scheduler, cancellationToken, continuationOptions, ref stackMark);
        }

        // Same as the above overload, just with a stack mark parameter.
        private Task ContinueWith(Action<Task, Object> continuationAction, Object state, TaskScheduler scheduler,
            CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, ref StackCrawlMark stackMark)
        {
            // Throw on continuation with null action
            if (continuationAction == null)
            {
                throw new ArgumentNullException("continuationAction");
            }

            // Throw on continuation with null TaskScheduler
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            TaskCreationOptions creationOptions;
            InternalTaskOptions internalOptions;
            CreationOptionsFromContinuationOptions(continuationOptions, out creationOptions, out internalOptions);

            Task continuationTask = new ContinuationTaskFromTask(
                this, continuationAction, state,
                creationOptions, internalOptions,
                ref stackMark
            );

            // Register the continuation.  If synchronous execution is requested, this may
            // actually invoke the continuation before returning.
            ContinueWithCore(continuationTask, scheduler, cancellationToken, continuationOptions);

            return continuationTask;
        }

        #endregion

        #region Func<Task, TResult> continuation

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, TaskScheduler.Current, default(CancellationToken),
                TaskContinuationOptions.None, ref stackMark);
        }


        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, TaskScheduler.Current, cancellationToken, TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes.  When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, scheduler, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed. If the continuation criteria specified through the <paramref
        /// name="continuationOptions"/> parameter are not met, the continuation task will be canceled
        /// instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, TaskContinuationOptions continuationOptions)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, TaskScheduler.Current, default(CancellationToken), continuationOptions, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task as an argument.
        /// </param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its
        /// execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed. If the criteria specified through the <paramref name="continuationOptions"/> parameter
        /// are not met, the continuation task will be canceled instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, CancellationToken cancellationToken,
                                                   TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, scheduler, cancellationToken, continuationOptions, ref stackMark);
        }

        // Same as the above overload, just with a stack mark parameter.
        private Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, TaskScheduler scheduler,
            CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, ref StackCrawlMark stackMark)
        {
            // Throw on continuation with null function
            if (continuationFunction == null)
            {
                throw new ArgumentNullException("continuationFunction");
            }

            // Throw on continuation with null task scheduler
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            TaskCreationOptions creationOptions;
            InternalTaskOptions internalOptions;
            CreationOptionsFromContinuationOptions(continuationOptions, out creationOptions, out internalOptions);

            Task<TResult> continuationTask = new ContinuationResultTaskFromTask<TResult>(
                this, continuationFunction, null,
                creationOptions, internalOptions,
                ref stackMark
            );

            // Register the continuation.  If synchronous execution is requested, this may
            // actually invoke the continuation before returning.
            ContinueWithCore(continuationTask, scheduler, cancellationToken, continuationOptions);

            return continuationTask;
        }
        #endregion

        #region Func<Task, Object, TResult> continuation

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation function.</param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, state, TaskScheduler.Current, default(CancellationToken),
                TaskContinuationOptions.None, ref stackMark);
        }


        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation function.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, state, TaskScheduler.Current, cancellationToken, TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes.  When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation function.</param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed, whether it completes due to running to completion successfully, faulting due to an
        /// unhandled exception, or exiting out early due to being canceled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, state, scheduler, default(CancellationToken), TaskContinuationOptions.None, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation function.</param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed. If the continuation criteria specified through the <paramref
        /// name="continuationOptions"/> parameter are not met, the continuation task will be canceled
        /// instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state, TaskContinuationOptions continuationOptions)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, state, TaskScheduler.Current, default(CancellationToken), continuationOptions, ref stackMark);
        }

        /// <summary>
        /// Creates a continuation that executes when the target <see cref="Task"/> completes.
        /// </summary>
        /// <typeparam name="TResult">
        /// The type of the result produced by the continuation.
        /// </typeparam>
        /// <param name="continuationFunction">
        /// A function to run when the <see cref="Task"/> completes. When run, the delegate will be
        /// passed the completed task and the caller-supplied state object as arguments.
        /// </param>
        /// <param name="state">An object representing data to be used by the continuation function.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that will be assigned to the new continuation task.</param>
        /// <param name="continuationOptions">
        /// Options for when the continuation is scheduled and how it behaves. This includes criteria, such
        /// as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.OnlyOnCanceled">OnlyOnCanceled</see>, as
        /// well as execution options, such as <see
        /// cref="System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously">ExecuteSynchronously</see>.
        /// </param>
        /// <param name="scheduler">
        /// The <see cref="TaskScheduler"/> to associate with the continuation task and to use for its
        /// execution.
        /// </param>
        /// <returns>A new continuation <see cref="Task{TResult}"/>.</returns>
        /// <remarks>
        /// The returned <see cref="Task{TResult}"/> will not be scheduled for execution until the current task has
        /// completed. If the criteria specified through the <paramref name="continuationOptions"/> parameter
        /// are not met, the continuation task will be canceled instead of scheduled.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="continuationFunction"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="continuationOptions"/> argument specifies an invalid value for <see
        /// cref="T:System.Threading.Tasks.TaskContinuationOptions">TaskContinuationOptions</see>.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="scheduler"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The provided <see cref="System.Threading.CancellationToken">CancellationToken</see>
        /// has already been disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state, CancellationToken cancellationToken,
                                                   TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return ContinueWith<TResult>(continuationFunction, state, scheduler, cancellationToken, continuationOptions, ref stackMark);
        }

        // Same as the above overload, just with a stack mark parameter.
        private Task<TResult> ContinueWith<TResult>(Func<Task, Object, TResult> continuationFunction, Object state, TaskScheduler scheduler,
            CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, ref StackCrawlMark stackMark)
        {
            // Throw on continuation with null function
            if (continuationFunction == null)
            {
                throw new ArgumentNullException("continuationFunction");
            }

            // Throw on continuation with null task scheduler
            if (scheduler == null)
            {
                throw new ArgumentNullException("scheduler");
            }
            Contract.EndContractBlock();

            TaskCreationOptions creationOptions;
            InternalTaskOptions internalOptions;
            CreationOptionsFromContinuationOptions(continuationOptions, out creationOptions, out internalOptions);

            Task<TResult> continuationTask = new ContinuationResultTaskFromTask<TResult>(
                this, continuationFunction, state,
                creationOptions, internalOptions,
                ref stackMark
            );

            // Register the continuation.  If synchronous execution is requested, this may
            // actually invoke the continuation before returning.
            ContinueWithCore(continuationTask, scheduler, cancellationToken, continuationOptions);

            return continuationTask;
        }
        #endregion

        /// <summary>
        /// Converts TaskContinuationOptions to TaskCreationOptions, and also does
        /// some validity checking along the way.
        /// </summary>
        /// <param name="continuationOptions">Incoming TaskContinuationOptions</param>
        /// <param name="creationOptions">Outgoing TaskCreationOptions</param>
        /// <param name="internalOptions">Outgoing InternalTaskOptions</param>
        internal static void CreationOptionsFromContinuationOptions(
            TaskContinuationOptions continuationOptions,
            out TaskCreationOptions creationOptions,
            out InternalTaskOptions internalOptions)
        {
            // This is used a couple of times below
            TaskContinuationOptions NotOnAnything =
                TaskContinuationOptions.NotOnCanceled |
                TaskContinuationOptions.NotOnFaulted |
                TaskContinuationOptions.NotOnRanToCompletion;

            TaskContinuationOptions creationOptionsMask =
                TaskContinuationOptions.PreferFairness |
                TaskContinuationOptions.LongRunning |
                TaskContinuationOptions.DenyChildAttach |
                TaskContinuationOptions.HideScheduler |
                TaskContinuationOptions.AttachedToParent|
                TaskContinuationOptions.RunContinuationsAsynchronously;

            // Check that LongRunning and ExecuteSynchronously are not specified together
            TaskContinuationOptions illegalMask = TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LongRunning;
            if ((continuationOptions & illegalMask) == illegalMask)
            {
                throw new ArgumentOutOfRangeException("continuationOptions", Environment.GetResourceString("Task_ContinueWith_ESandLR"));
            }

            // Check that no illegal options were specified
            if ((continuationOptions &
                ~(creationOptionsMask | NotOnAnything | 
                    TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.ExecuteSynchronously)) != 0)
            {
                throw new ArgumentOutOfRangeException("continuationOptions");
            }

            // Check that we didn't specify "not on anything"
            if ((continuationOptions & NotOnAnything) == NotOnAnything)
            {
                throw new ArgumentOutOfRangeException("continuationOptions", Environment.GetResourceString("Task_ContinueWith_NotOnAnything"));
            }

            // This passes over all but LazyCancellation, which has no representation in TaskCreationOptions
            creationOptions = (TaskCreationOptions)(continuationOptions & creationOptionsMask);

            // internalOptions has at least ContinuationTask ...
            internalOptions = InternalTaskOptions.ContinuationTask;

            // ... and possibly LazyCancellation
            if ((continuationOptions & TaskContinuationOptions.LazyCancellation) != 0)
                internalOptions |= InternalTaskOptions.LazyCancellation;
        }


        /// <summary>
        /// Registers the continuation and possibly runs it (if the task is already finished).
        /// </summary>
        /// <param name="continuationTask">The continuation task itself.</param>
        /// <param name="scheduler">TaskScheduler with which to associate continuation task.</param>
        /// <param name="options">Restrictions on when the continuation becomes active.</param>
        internal void ContinueWithCore(Task continuationTask,
                                       TaskScheduler scheduler,
                                       CancellationToken cancellationToken,
                                       TaskContinuationOptions options)
        {
            Contract.Requires(continuationTask != null, "Task.ContinueWithCore(): null continuationTask");
            Contract.Requires(scheduler != null, "Task.ContinueWithCore(): null scheduler");
            Contract.Requires(!continuationTask.IsCompleted, "Did not expect continuationTask to be completed");

            // Create a TaskContinuation
            TaskContinuation continuation = new StandardTaskContinuation(continuationTask, options, scheduler);

            // If cancellationToken is cancellable, then assign it.  
            if (cancellationToken.CanBeCanceled)
            {
                if (IsCompleted || cancellationToken.IsCancellationRequested)
                {
                    // If the antecedent has completed, then we will not be queuing up
                    // the continuation in the antecedent's continuation list.  Likewise,
                    // if the cancellationToken has been canceled, continuationTask will
                    // be completed in the AssignCancellationToken call below, and there
                    // is no need to queue the continuation to the antecedent's continuation
                    // list.  In either of these two cases, we will pass "null" for the antecedent,
                    // meaning "the cancellation callback should not attempt to remove the
                    // continuation from its antecedent's continuation list".
                    continuationTask.AssignCancellationToken(cancellationToken, null, null);
                }
                else
                {
                    // The antecedent is not yet complete, so there is a pretty good chance
                    // that the continuation will be queued up in the antecedent.  Assign the
                    // cancellation token with information about the antecedent, so that the
                    // continuation can be dequeued upon the signalling of the token.
                    //
                    // It's possible that the antecedent completes before the call to AddTaskContinuation,
                    // and that is a benign race condition.  It just means that the cancellation will result in
                    // a futile search of the antecedent's continuation list.
                    continuationTask.AssignCancellationToken(cancellationToken, this, continuation);
                }
            }

            // In the case of a pre-canceled token, continuationTask will have been completed
            // in a Canceled state by now.  If such is the case, there is no need to go through
            // the motions of queuing up the continuation for eventual execution.
            if (!continuationTask.IsCompleted)
            {
                // We need additional correlation produced here to ensure that at least the continuation 
                // code will be correlatable to the currrent activity that initiated "this" task:
                //  . when the antecendent ("this") is a promise we have very little control over where 
                //    the code for the promise will run (e.g. it can be a task from a user provided 
                //    TaskCompletionSource or from a classic Begin/End async operation); this user or 
                //    system code will likely not have stamped an activity id on the thread, so there's
                //    generally no easy correlation that can be provided between the current activity
                //    and the promise. Also the continuation code may run practically on any thread. 
                //    Since there may be no correlation between the current activity and the TCS's task
                //    activity, we ensure we at least create a correlation from the current activity to
                //    the continuation that runs when the promise completes.
                if ((this.Options & (TaskCreationOptions)InternalTaskOptions.PromiseTask) != 0 &&
                    !(this is ITaskCompletionAction))
                {
                    var etwLog = TplEtwProvider.Log;
                    if (etwLog.IsEnabled())
                    {
                        etwLog.AwaitTaskContinuationScheduled(TaskScheduler.Current.Id, Task.CurrentId ?? 0, continuationTask.Id);
                    }
                }

                // Attempt to enqueue the continuation
                bool continuationQueued = AddTaskContinuation(continuation, addBeforeOthers: false);

                // If the continuation was not queued (because the task completed), then run it now.
                if (!continuationQueued) continuation.Run(this, bCanInlineContinuationTask: true);
            }
        }
        #endregion

        // Adds a lightweight completion action to a task.  This is similar to a continuation
        // task except that it is stored as an action, and thus does not require the allocation/
        // execution resources of a continuation task.
        //
        // Used internally by ContinueWhenAll() and ContinueWhenAny().
        internal void AddCompletionAction(ITaskCompletionAction action)
        {
            AddCompletionAction(action, addBeforeOthers: false);
        }

        private void AddCompletionAction(ITaskCompletionAction action, bool addBeforeOthers)
        {
            if (!AddTaskContinuation(action, addBeforeOthers))
                action.Invoke(this); // run the action directly if we failed to queue the continuation (i.e., the task completed)
        }

        // Support method for AddTaskContinuation that takes care of multi-continuation logic.
        // Returns true if and only if the continuation was successfully queued.
        // THIS METHOD ASSUMES THAT m_continuationObject IS NOT NULL.  That case was taken
        // care of in the calling method, AddTaskContinuation().
        private bool AddTaskContinuationComplex(object tc, bool addBeforeOthers)
        {
            Contract.Requires(tc != null, "Expected non-null tc object in AddTaskContinuationComplex");

            object oldValue = m_continuationObject;

            // Logic for the case where we were previously storing a single continuation
            if ((oldValue != s_taskCompletionSentinel) && (!(oldValue is List<object>)))
            {
                // Construct a new TaskContinuation list
                List<object> newList = new List<object>();

                // Add in the old single value
                newList.Add(oldValue);

                // Now CAS in the new list
                Interlocked.CompareExchange(ref m_continuationObject, newList, oldValue);

                // We might be racing against another thread converting the single into
                // a list, or we might be racing against task completion, so resample "list"
                // below.
            }

            // m_continuationObject is guaranteed at this point to be either a List or
            // s_taskCompletionSentinel.
            List<object> list = m_continuationObject as List<object>;
            Contract.Assert((list != null) || (m_continuationObject == s_taskCompletionSentinel),
                "Expected m_continuationObject to be list or sentinel");

            // If list is null, it can only mean that s_taskCompletionSentinel has been exchanged
            // into m_continuationObject.  Thus, the task has completed and we should return false
            // from this method, as we will not be queuing up the continuation.
            if (list != null)
            {
                lock (list)
                {
                    // It is possible for the task to complete right after we snap the copy of
                    // the list.  If so, then fall through and return false without queuing the
                    // continuation.
                    if (m_continuationObject != s_taskCompletionSentinel)
                    {
                        // Before growing the list we remove possible null entries that are the
                        // result from RemoveContinuations()
                        if (list.Count == list.Capacity)
                        {
                            list.RemoveAll(s_IsTaskContinuationNullPredicate);
                        }

                        if (addBeforeOthers)
                            list.Insert(0, tc);
                        else
                            list.Add(tc);

                        return true; // continuation successfully queued, so return true.
                    }
                }
            }

            // We didn't succeed in queuing the continuation, so return false.
            return false;
        }

        // Record a continuation task or action.
        // Return true if and only if we successfully queued a continuation.
        private bool AddTaskContinuation(object tc, bool addBeforeOthers)
        {
            Contract.Requires(tc != null);

            // Make sure that, if someone calls ContinueWith() right after waiting for the predecessor to complete,
            // we don't queue up a continuation.
            if (IsCompleted) return false;

            // Try to just jam tc into m_continuationObject
            if ((m_continuationObject != null) || (Interlocked.CompareExchange(ref m_continuationObject, tc, null) != null))
            {
                // If we get here, it means that we failed to CAS tc into m_continuationObject.
                // Therefore, we must go the more complicated route.
                return AddTaskContinuationComplex(tc, addBeforeOthers);
            }
            else return true;
        }

        // Removes a continuation task from m_continuations
        internal void RemoveContinuation(object continuationObject) // could be TaskContinuation or Action<Task>
        {
            // We need to snap a local reference to m_continuations since reading a volatile object is more costly.
            // Also to prevent the value to be changed as result of a race condition with another method.
            object continuationsLocalRef = m_continuationObject;

            // Task is completed. Nothing to do here.
            if (continuationsLocalRef == s_taskCompletionSentinel) return;

            List<object> continuationsLocalListRef = continuationsLocalRef as List<object>;

            if (continuationsLocalListRef == null)
            {
                // This is not a list. If we have a single object (the one we want to remove) we try to replace it with an empty list.
                // Note we cannot go back to a null state, since it will mess up the AddTaskContinuation logic.
                if (Interlocked.CompareExchange(ref m_continuationObject, new List<object>(), continuationObject) != continuationObject)
                {
                    // If we fail it means that either AddContinuationComplex won the race condition and m_continuationObject is now a List
                    // that contains the element we want to remove. Or FinishContinuations set the s_taskCompletionSentinel.
                    // So we should try to get a list one more time
                    continuationsLocalListRef = m_continuationObject as List<object>;
                }
                else
                {
                    // Exchange was successful so we can skip the last comparison
                    return;
                }
            }

            // if continuationsLocalRef == null it means s_taskCompletionSentinel has been set already and there is nothing else to do.
            if (continuationsLocalListRef != null)
            {
                lock (continuationsLocalListRef)
                {
                    // There is a small chance that this task completed since we took a local snapshot into
                    // continuationsLocalRef.  In that case, just return; we don't want to be manipulating the
                    // continuation list as it is being processed.
                    if (m_continuationObject == s_taskCompletionSentinel) return;

                    // Find continuationObject in the continuation list
                    int index = continuationsLocalListRef.IndexOf(continuationObject);

                    if (index != -1)
                    {
                        // null out that TaskContinuation entry, which will be interpreted as "to be cleaned up"
                        continuationsLocalListRef[index] = null;

                    }
                }
            }
        }

        // statically allocated delegate for the RemoveAll expression in RemoveContinuations() and AddContinuationComplex()
        private readonly static Predicate<object> s_IsTaskContinuationNullPredicate =
            new Predicate<object>((tc) => { return (tc == null); });


        //
        // Wait methods
        //

        /// <summary>
        /// Waits for all of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// At least one of the <see cref="Task"/> instances was canceled -or- an exception was thrown during
        /// the execution of at least one of the <see cref="Task"/> instances.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static void WaitAll(params Task[] tasks)
        {
#if DEBUG
            bool waitResult =
#endif
            WaitAll(tasks, Timeout.Infinite);

#if DEBUG
            Contract.Assert(waitResult, "expected wait to succeed");
#endif
        }

        /// <summary>
        /// Waits for all of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <returns>
        /// true if all of the <see cref="Task"/> instances completed execution within the allotted time;
        /// otherwise, false.
        /// </returns>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="timeout">
        /// A <see cref="System.TimeSpan"/> that represents the number of milliseconds to wait, or a <see
        /// cref="System.TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// At least one of the <see cref="Task"/> instances was canceled -or- an exception was thrown during
        /// the execution of at least one of the <see cref="Task"/> instances.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents an
        /// infinite time-out -or- timeout is greater than
        /// <see cref="System.Int32.MaxValue"/>.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static bool WaitAll(Task[] tasks, TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }

            return WaitAll(tasks, (int)totalMilliseconds);

        }

        /// <summary>
        /// Waits for all of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <returns>
        /// true if all of the <see cref="Task"/> instances completed execution within the allotted time;
        /// otherwise, false.
        /// </returns>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.</param>
        /// <param name="tasks">An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// At least one of the <see cref="Task"/> instances was canceled -or- an exception was thrown during
        /// the execution of at least one of the <see cref="Task"/> instances.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static bool WaitAll(Task[] tasks, int millisecondsTimeout)
        {
            return WaitAll(tasks, millisecondsTimeout, default(CancellationToken));
        }

        /// <summary>
        /// Waits for all of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <returns>
        /// true if all of the <see cref="Task"/> instances completed execution within the allotted time;
        /// otherwise, false.
        /// </returns>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for the tasks to complete.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// At least one of the <see cref="Task"/> instances was canceled -or- an exception was thrown during
        /// the execution of at least one of the <see cref="Task"/> instances.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static void WaitAll(Task[] tasks, CancellationToken cancellationToken)
        {
            WaitAll(tasks, Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Waits for all of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <returns>
        /// true if all of the <see cref="Task"/> instances completed execution within the allotted time;
        /// otherwise, false.
        /// </returns>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for the tasks to complete.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.AggregateException">
        /// At least one of the <see cref="Task"/> instances was canceled -or- an exception was thrown during
        /// the execution of at least one of the <see cref="Task"/> instances.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static bool WaitAll(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (tasks == null)
            {
                throw new ArgumentNullException("tasks");
            }
            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException("millisecondsTimeout");
            }
            Contract.EndContractBlock();

            cancellationToken.ThrowIfCancellationRequested(); // early check before we make any allocations

            //
            // In this WaitAll() implementation we have 2 alternate code paths for a task to be handled:
            // CODEPATH1: skip an already completed task, CODEPATH2: actually wait on tasks
            // We make sure that the exception behavior of Task.Wait() is replicated the same for tasks handled in either of these codepaths
            //

            List<Exception> exceptions = null;
            List<Task> waitedOnTaskList = null;
            List<Task> notificationTasks = null;

            // If any of the waited-upon tasks end as Faulted or Canceled, set these to true.
            bool exceptionSeen = false, cancellationSeen = false;

            bool returnValue = true;

            // Collects incomplete tasks in "waitedOnTaskList"
            for (int i = tasks.Length - 1; i >= 0; i--)
            {
                Task task = tasks[i];

                if (task == null)
                {
                    throw new ArgumentException(Environment.GetResourceString("Task_WaitMulti_NullTask"), "tasks");
                }

                bool taskIsCompleted = task.IsCompleted;
                if (!taskIsCompleted)
                {
                    // try inlining the task only if we have an infinite timeout and an empty cancellation token
                    if (millisecondsTimeout != Timeout.Infinite || cancellationToken.CanBeCanceled)
                    {
                        // We either didn't attempt inline execution because we had a non-infinite timeout or we had a cancellable token.
                        // In all cases we need to do a full wait on the task (=> add its event into the list.)
                        AddToList(task, ref waitedOnTaskList, initSize: tasks.Length);
                    }
                    else
                    {
                        // We are eligible for inlining.  If it doesn't work, we'll do a full wait.
                        taskIsCompleted = task.WrappedTryRunInline() && task.IsCompleted; // A successful TryRunInline doesn't guarantee completion
                        if (!taskIsCompleted) AddToList(task, ref waitedOnTaskList, initSize: tasks.Length);
                    }
                }

                if (taskIsCompleted)
                {
                    if (task.IsFaulted) exceptionSeen = true;
                    else if (task.IsCanceled) cancellationSeen = true;
                    if (task.IsWaitNotificationEnabled) AddToList(task, ref notificationTasks, initSize: 1);
                }
            }

            if (waitedOnTaskList != null)
            {
                // Block waiting for the tasks to complete.
                returnValue = WaitAllBlockingCore(waitedOnTaskList, millisecondsTimeout, cancellationToken);

                // If the wait didn't time out, ensure exceptions are propagated, and if a debugger is
                // attached and one of these tasks requires it, that we notify the debugger of a wait completion.
                if (returnValue)
                {
                    // Add any exceptions for this task to the collection, and if it's wait
                    // notification bit is set, store it to operate on at the end.
                    foreach (var task in waitedOnTaskList)
                    {
                        if (task.IsFaulted) exceptionSeen = true;
                        else if (task.IsCanceled) cancellationSeen = true;
                        if (task.IsWaitNotificationEnabled) AddToList(task, ref notificationTasks, initSize: 1);
                    }
                }

                // We need to prevent the tasks array from being GC'ed until we come out of the wait.
                // This is necessary so that the Parallel Debugger can traverse it during the long wait and 
                // deduce waiter/waitee relationships
                GC.KeepAlive(tasks);
            }

            // Now that we're done and about to exit, if the wait completed and if we have 
            // any tasks with a notification bit set, signal the debugger if any requires it.
            if (returnValue && notificationTasks != null)
            {
                // Loop through each task tha that had its bit set, and notify the debugger
                // about the first one that requires it.  The debugger will reset the bit
                // for any tasks we don't notify of as soon as we break, so we only need to notify
                // for one.
                foreach (var task in notificationTasks)
                {
                    if (task.NotifyDebuggerOfWaitCompletionIfNecessary()) break;
                }
            }

            // If one or more threw exceptions, aggregate and throw them.
            if (returnValue && (exceptionSeen || cancellationSeen))
            {
                // If the WaitAll was canceled and tasks were canceled but not faulted, 
                // prioritize throwing an OCE for canceling the WaitAll over throwing an 
                // AggregateException for all of the canceled Tasks.  This helps
                // to bring determinism to an otherwise non-determistic case of using
                // the same token to cancel both the WaitAll and the Tasks.
                if (!exceptionSeen) cancellationToken.ThrowIfCancellationRequested();

                // Now gather up and throw all of the exceptions.
                foreach (var task in tasks) AddExceptionsForCompletedTask(ref exceptions, task);
                Contract.Assert(exceptions != null, "Should have seen at least one exception");
                throw new AggregateException(exceptions);
            }

            return returnValue;
        }

        /// <summary>Adds an element to the list, initializing the list if it's null.</summary>
        /// <typeparam name="T">Specifies the type of data stored in the list.</typeparam>
        /// <param name="item">The item to add.</param>
        /// <param name="list">The list.</param>
        /// <param name="initSize">The size to which to initialize the list if the list is null.</param>
        private static void AddToList<T>(T item, ref List<T> list, int initSize)
        {
            if (list == null) list = new List<T>(initSize);
            list.Add(item);
        }

        /// <summary>Performs a blocking WaitAll on the vetted list of tasks.</summary>
        /// <param name="tasks">The tasks, which have already been checked and filtered for completion.</param>
        /// <param name="millisecondsTimeout">The timeout.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>true if all of the tasks completed; otherwise, false.</returns>
        private static bool WaitAllBlockingCore(List<Task> tasks, int millisecondsTimeout, CancellationToken cancellationToken)
        {
            Contract.Assert(tasks != null, "Expected a non-null list of tasks");
            Contract.Assert(tasks.Count > 0, "Expected at least one task");

            bool waitCompleted = false;
            var mres = new SetOnCountdownMres(tasks.Count);
            try
            {
                foreach (var task in tasks)
                {
                    task.AddCompletionAction(mres, addBeforeOthers: true);
                }
                waitCompleted = mres.Wait(millisecondsTimeout, cancellationToken);
            }
            finally
            {
                if (!waitCompleted)
                {
                    foreach (var task in tasks)
                    {
                        if (!task.IsCompleted) task.RemoveContinuation(mres);
                    }
                }
                // It's ok that we don't dispose of the MRES here, as we never
                // access the MRES' WaitHandle, and thus no finalizable resources
                // are actually created.  We don't always just Dispose it because
                // a continuation that's accessing the MRES could still be executing.
            }
            return waitCompleted;
        }

        // A ManualResetEventSlim that will get Set after Invoke is called count times.
        // This allows us to replace this logic:
        //      var mres = new ManualResetEventSlim(tasks.Count);
        //      Action<Task> completionAction = delegate { if(Interlocked.Decrement(ref count) == 0) mres.Set(); };
        //      foreach(var task in tasks) task.AddCompletionAction(completionAction);
        // with this logic:
        //      var mres = new SetOnCountdownMres(tasks.Count);
        //      foreach(var task in tasks) task.AddCompletionAction(mres);
        // which saves a couple of allocations.
        //
        // Used in WaitAllBlockingCore (above).
        private sealed class SetOnCountdownMres : ManualResetEventSlim, ITaskCompletionAction
        {
            private int _count;

            internal SetOnCountdownMres(int count)
            {
                Contract.Assert(count > 0, "Expected count > 0");
                _count = count;
            }

            public void Invoke(Task completingTask)
            {
                if (Interlocked.Decrement(ref _count) == 0) Set();
                Contract.Assert(_count >= 0, "Count should never go below 0");
            }

            public bool InvokeMayRunArbitraryCode { get { return false; } }
        }

        /// <summary>
        /// Internal WaitAll implementation which is meant to be used with small number of tasks,
        /// optimized for Parallel.Invoke and other structured primitives.
        /// </summary>
        internal static void FastWaitAll(Task[] tasks)
        {
            Contract.Requires(tasks != null);

            List<Exception> exceptions = null;

            // Collects incomplete tasks in "waitedOnTaskList" and their cooperative events in "cooperativeEventList"
            for (int i = tasks.Length - 1; i >= 0; i--)
            {
                if (!tasks[i].IsCompleted)
                {
                    // Just attempting to inline here... result doesn't matter.
                    // We'll do a second pass to do actual wait on each task, and to aggregate their exceptions.
                    // If the task is inlined here, it will register as IsCompleted in the second pass
                    // and will just give us the exception.
                    tasks[i].WrappedTryRunInline();
                }
            }

            // Wait on the tasks.
            for (int i = tasks.Length - 1; i >= 0; i--)
            {
                var task = tasks[i];
                task.SpinThenBlockingWait(Timeout.Infinite, default(CancellationToken));
                AddExceptionsForCompletedTask(ref exceptions, task);

                // Note that unlike other wait code paths, we do not check
                // task.NotifyDebuggerOfWaitCompletionIfNecessary() here, because this method is currently
                // only used from contexts where the tasks couldn't have that bit set, namely
                // Parallel.Invoke.  If that ever changes, such checks should be added here.
            }

            // If one or more threw exceptions, aggregate them.
            if (exceptions != null)
            {
                throw new AggregateException(exceptions);
            }
        }

        /// <summary>
        /// This internal function is only meant to be called by WaitAll()
        /// If the completed task is canceled or it has other exceptions, here we will add those
        /// into the passed in exception list (which will be lazily initialized here).
        /// </summary>
        internal static void AddExceptionsForCompletedTask(ref List<Exception> exceptions, Task t)
        {
            AggregateException ex = t.GetExceptions(true);
            if (ex != null)
            {
                // make sure the task's exception observed status is set appropriately
                // it's possible that WaitAll was called by the parent of an attached child,
                // this will make sure it won't throw again in the implicit wait
                t.UpdateExceptionObservedStatus();

                if (exceptions == null)
                {
                    exceptions = new List<Exception>(ex.InnerExceptions.Count);
                }

                exceptions.AddRange(ex.InnerExceptions);
            }
        }


        /// <summary>
        /// Waits for any of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <returns>The index of the completed task in the <paramref name="tasks"/> array argument.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static int WaitAny(params Task[] tasks)
        {
            int waitResult = WaitAny(tasks, Timeout.Infinite);
            Contract.Assert(tasks.Length == 0 || waitResult != -1, "expected wait to succeed");
            return waitResult;
        }

        /// <summary>
        /// Waits for any of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="timeout">
        /// A <see cref="System.TimeSpan"/> that represents the number of milliseconds to wait, or a <see
        /// cref="System.TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <returns>
        /// The index of the completed task in the <paramref name="tasks"/> array argument, or -1 if the
        /// timeout occurred.
        /// </returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents an
        /// infinite time-out -or- timeout is greater than
        /// <see cref="System.Int32.MaxValue"/>.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static int WaitAny(Task[] tasks, TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }

            return WaitAny(tasks, (int)totalMilliseconds);
        }

        /// <summary>
        /// Waits for any of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for a task to complete.
        /// </param>
        /// <returns>
        /// The index of the completed task in the <paramref name="tasks"/> array argument.
        /// </returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static int WaitAny(Task[] tasks, CancellationToken cancellationToken)
        {
            return WaitAny(tasks, Timeout.Infinite, cancellationToken);
        }

        /// <summary>
        /// Waits for any of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.
        /// </param>
        /// <returns>
        /// The index of the completed task in the <paramref name="tasks"/> array argument, or -1 if the
        /// timeout occurred.
        /// </returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static int WaitAny(Task[] tasks, int millisecondsTimeout)
        {
            return WaitAny(tasks, millisecondsTimeout, default(CancellationToken));
        }

        /// <summary>
        /// Waits for any of the provided <see cref="Task"/> objects to complete execution.
        /// </summary>
        /// <param name="tasks">
        /// An array of <see cref="Task"/> instances on which to wait.
        /// </param>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite"/> (-1) to
        /// wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> to observe while waiting for a task to complete.
        /// </param>
        /// <returns>
        /// The index of the completed task in the <paramref name="tasks"/> array argument, or -1 if the
        /// timeout occurred.
        /// </returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> argument contains a null element.
        /// </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an
        /// infinite time-out.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException">
        /// The <paramref name="cancellationToken"/> was canceled.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoOptimization)]  // this is needed for the parallel debugger
        public static int WaitAny(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (tasks == null)
            {
                throw new ArgumentNullException("tasks");
            }
            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException("millisecondsTimeout");
            }
            Contract.EndContractBlock();

            cancellationToken.ThrowIfCancellationRequested(); // early check before we make any allocations

            int signaledTaskIndex = -1;

            // Make a pass through the loop to check for any tasks that may have
            // already been completed, and to verify that no tasks are null.

            for (int taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                Task task = tasks[taskIndex];

                if (task == null)
                {
                    throw new ArgumentException(Environment.GetResourceString("Task_WaitMulti_NullTask"), "tasks");
                }

                if (signaledTaskIndex == -1 && task.IsCompleted)
                {
                    // We found our first completed task.  Store it, but we can't just return here,
                    // as we still need to validate the whole array for nulls.
                    signaledTaskIndex = taskIndex;
                }
            }

            if (signaledTaskIndex == -1 && tasks.Length != 0)
            {
                Task<Task> firstCompleted = TaskFactory.CommonCWAnyLogic(tasks);
                bool waitCompleted = firstCompleted.Wait(millisecondsTimeout, cancellationToken);
                if (waitCompleted)
                {
                    Contract.Assert(firstCompleted.Status == TaskStatus.RanToCompletion);
                    signaledTaskIndex = Array.IndexOf(tasks, firstCompleted.Result);
                    Contract.Assert(signaledTaskIndex >= 0);
                }
            }

            // We need to prevent the tasks array from being GC'ed until we come out of the wait.
            // This is necessary so that the Parallel Debugger can traverse it during the long wait 
            // and deduce waiter/waitee relationships
            GC.KeepAlive(tasks);

            // Return the index
            return signaledTaskIndex;
        }

        #region FromResult / FromException / FromCancellation

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed successfully with the specified result.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="result">The result to store into the completed task.</param>
        /// <returns>The successfully completed task.</returns>
        public static Task<TResult> FromResult<TResult>(TResult result)
        {
            return new Task<TResult>(result);
        }

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed exceptionally with the specified exception.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="exception">The exception with which to complete the task.</param>
        /// <returns>The faulted task.</returns>
        public static Task FromException(Exception exception)
        {
            return FromException<VoidTaskResult>(exception);
        }

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed exceptionally with the specified exception.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="exception">The exception with which to complete the task.</param>
        /// <returns>The faulted task.</returns>
        public static Task<TResult> FromException<TResult>(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException("exception");
            Contract.EndContractBlock();

            var task = new Task<TResult>();
            bool succeeded = task.TrySetException(exception);
            Contract.Assert(succeeded, "This should always succeed on a new task.");
            return task;
        }

        /// <summary>Creates a <see cref="Task"/> that's completed due to cancellation with the specified token.</summary>
        /// <param name="cancellationToken">The token with which to complete the task.</param>
        /// <returns>The canceled task.</returns>
        [FriendAccessAllowed]
        internal static Task FromCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested) throw new ArgumentOutOfRangeException("cancellationToken");
            Contract.EndContractBlock();
            return new Task(true, TaskCreationOptions.None, cancellationToken);
        }

        /// <summary>Creates a <see cref="Task"/> that's completed due to cancellation with the specified token.</summary>
        /// <param name="cancellationToken">The token with which to complete the task.</param>
        /// <returns>The canceled task.</returns>
        public static Task FromCanceled(CancellationToken cancellationToken)
        {
            return FromCancellation(cancellationToken);
        }

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed due to cancellation with the specified token.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="cancellationToken">The token with which to complete the task.</param>
        /// <returns>The canceled task.</returns>
        [FriendAccessAllowed]
        internal static Task<TResult> FromCancellation<TResult>(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested) throw new ArgumentOutOfRangeException("cancellationToken");
            Contract.EndContractBlock();
            return new Task<TResult>(true, default(TResult), TaskCreationOptions.None, cancellationToken);
        }

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed due to cancellation with the specified token.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="cancellationToken">The token with which to complete the task.</param>
        /// <returns>The canceled task.</returns>
        public static Task<TResult> FromCanceled<TResult>(CancellationToken cancellationToken)
        {
            return FromCancellation<TResult>(cancellationToken);
        }

        /// <summary>Creates a <see cref="Task{TResult}"/> that's completed due to cancellation with the specified exception.</summary>
        /// <typeparam name="TResult">The type of the result returned by the task.</typeparam>
        /// <param name="exception">The exception with which to complete the task.</param>
        /// <returns>The canceled task.</returns>
        internal static Task<TResult> FromCancellation<TResult>(OperationCanceledException exception)
        {
            if (exception == null) throw new ArgumentNullException("exception");
            Contract.EndContractBlock();

            var task = new Task<TResult>();
            bool succeeded = task.TrySetCanceled(exception.CancellationToken, exception);
            Contract.Assert(succeeded, "This should always succeed on a new task.");
            return task;
        }
        #endregion

        #region Run methods


        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a Task handle for that work.
        /// </summary>
        /// <param name="action">The work to execute asynchronously</param>
        /// <returns>A Task that represents the work queued to execute in the ThreadPool.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> parameter was null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public static Task Run(Action action)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Task.InternalStartNew(null, action, null, default(CancellationToken), TaskScheduler.Default,
                TaskCreationOptions.DenyChildAttach, InternalTaskOptions.None, ref stackMark);
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a Task handle for that work.
        /// </summary>
        /// <param name="action">The work to execute asynchronously</param>
        /// <param name="cancellationToken">A cancellation token that should be used to cancel the work</param>
        /// <returns>A Task that represents the work queued to execute in the ThreadPool.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="action"/> parameter was null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The <see cref="T:System.CancellationTokenSource"/> associated with <paramref name="cancellationToken"/> was disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public static Task Run(Action action, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Task.InternalStartNew(null, action, null, cancellationToken, TaskScheduler.Default,
                TaskCreationOptions.DenyChildAttach, InternalTaskOptions.None, ref stackMark);
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a Task(TResult) handle for that work.
        /// </summary>
        /// <param name="function">The work to execute asynchronously</param>
        /// <returns>A Task(TResult) that represents the work queued to execute in the ThreadPool.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public static Task<TResult> Run<TResult>(Func<TResult> function)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Task<TResult>.StartNew(null, function, default(CancellationToken),
                TaskCreationOptions.DenyChildAttach, InternalTaskOptions.None, TaskScheduler.Default, ref stackMark);
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a Task(TResult) handle for that work.
        /// </summary>
        /// <param name="function">The work to execute asynchronously</param>
        /// <param name="cancellationToken">A cancellation token that should be used to cancel the work</param>
        /// <returns>A Task(TResult) that represents the work queued to execute in the ThreadPool.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The <see cref="T:System.CancellationTokenSource"/> associated with <paramref name="cancellationToken"/> was disposed.
        /// </exception>
        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        public static Task<TResult> Run<TResult>(Func<TResult> function, CancellationToken cancellationToken)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Task<TResult>.StartNew(null, function, cancellationToken,
                TaskCreationOptions.DenyChildAttach, InternalTaskOptions.None, TaskScheduler.Default, ref stackMark);
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a proxy for the
        /// Task returned by <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The work to execute asynchronously</param>
        /// <returns>A Task that represents a proxy for the Task returned by <paramref name="function"/>.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        public static Task Run(Func<Task> function)
        {
            return Run(function, default(CancellationToken));
        }


        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a proxy for the
        /// Task returned by <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The work to execute asynchronously</param>
        /// <param name="cancellationToken">A cancellation token that should be used to cancel the work</param>
        /// <returns>A Task that represents a proxy for the Task returned by <paramref name="function"/>.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The <see cref="T:System.CancellationTokenSource"/> associated with <paramref name="cancellationToken"/> was disposed.
        /// </exception>
        public static Task Run(Func<Task> function, CancellationToken cancellationToken)
        {
            // Check arguments
            if (function == null) throw new ArgumentNullException("function");
            Contract.EndContractBlock();

            if (AppContextSwitches.ThrowExceptionIfDisposedCancellationTokenSource)
            {
                cancellationToken.ThrowIfSourceDisposed();
            }

            // Short-circuit if we are given a pre-canceled token
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCancellation(cancellationToken);

            // Kick off initial Task, which will call the user-supplied function and yield a Task.
            Task<Task> task1 = Task<Task>.Factory.StartNew(function, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

            // Create a promise-style Task to be used as a proxy for the operation
            // Set lookForOce == true so that unwrap logic can be on the lookout for OCEs thrown as faults from task1, to support in-delegate cancellation.
            UnwrapPromise<VoidTaskResult> promise = new UnwrapPromise<VoidTaskResult>(task1, lookForOce: true);

            return promise;
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a proxy for the
        /// Task(TResult) returned by <paramref name="function"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result returned by the proxy Task.</typeparam>
        /// <param name="function">The work to execute asynchronously</param>
        /// <returns>A Task(TResult) that represents a proxy for the Task(TResult) returned by <paramref name="function"/>.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function)
        {
            return Run(function, default(CancellationToken));
        }

        /// <summary>
        /// Queues the specified work to run on the ThreadPool and returns a proxy for the
        /// Task(TResult) returned by <paramref name="function"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result returned by the proxy Task.</typeparam>
        /// <param name="function">The work to execute asynchronously</param>
        /// <param name="cancellationToken">A cancellation token that should be used to cancel the work</param>
        /// <returns>A Task(TResult) that represents a proxy for the Task(TResult) returned by <paramref name="function"/>.</returns>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="function"/> parameter was null.
        /// </exception>
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function, CancellationToken cancellationToken)
        {
            // Check arguments
            if (function == null) throw new ArgumentNullException("function");
            Contract.EndContractBlock();

            if (AppContextSwitches.ThrowExceptionIfDisposedCancellationTokenSource)
            {
                cancellationToken.ThrowIfSourceDisposed();
            }

            // Short-circuit if we are given a pre-canceled token
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCancellation<TResult>(cancellationToken);

            // Kick off initial Task, which will call the user-supplied function and yield a Task.
            Task<Task<TResult>> task1 = Task<Task<TResult>>.Factory.StartNew(function, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

            // Create a promise-style Task to be used as a proxy for the operation
            // Set lookForOce == true so that unwrap logic can be on the lookout for OCEs thrown as faults from task1, to support in-delegate cancellation.
            UnwrapPromise<TResult> promise = new UnwrapPromise<TResult>(task1, lookForOce: true);

            return promise;
        }


        #endregion

        #region Delay methods

        /// <summary>
        /// Creates a Task that will complete after a time delay.
        /// </summary>
        /// <param name="delay">The time span to wait before completing the returned Task</param>
        /// <returns>A Task that represents the time delay</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="delay"/> is less than -1 or greater than Int32.MaxValue.
        /// </exception>
        /// <remarks>
        /// After the specified time delay, the Task is completed in RanToCompletion state.
        /// </remarks>
        public static Task Delay(TimeSpan delay)
        {
            return Delay(delay, default(CancellationToken));
        }

        /// <summary>
        /// Creates a Task that will complete after a time delay.
        /// </summary>
        /// <param name="delay">The time span to wait before completing the returned Task</param>
        /// <param name="cancellationToken">The cancellation token that will be checked prior to completing the returned Task</param>
        /// <returns>A Task that represents the time delay</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="delay"/> is less than -1 or greater than Int32.MaxValue.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The provided <paramref name="cancellationToken"/> has already been disposed.
        /// </exception>        
        /// <remarks>
        /// If the cancellation token is signaled before the specified time delay, then the Task is completed in
        /// Canceled state.  Otherwise, the Task is completed in RanToCompletion state once the specified time
        /// delay has expired.
        /// </remarks>        
        public static Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            long totalMilliseconds = (long)delay.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("delay", Environment.GetResourceString("Task_Delay_InvalidDelay"));
            }

            return Delay((int)totalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Creates a Task that will complete after a time delay.
        /// </summary>
        /// <param name="millisecondsDelay">The number of milliseconds to wait before completing the returned Task</param>
        /// <returns>A Task that represents the time delay</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="millisecondsDelay"/> is less than -1.
        /// </exception>
        /// <remarks>
        /// After the specified time delay, the Task is completed in RanToCompletion state.
        /// </remarks>
        public static Task Delay(int millisecondsDelay)
        {
            return Delay(millisecondsDelay, default(CancellationToken));
        }

        /// <summary>
        /// Creates a Task that will complete after a time delay.
        /// </summary>
        /// <param name="millisecondsDelay">The number of milliseconds to wait before completing the returned Task</param>
        /// <param name="cancellationToken">The cancellation token that will be checked prior to completing the returned Task</param>
        /// <returns>A Task that represents the time delay</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// The <paramref name="millisecondsDelay"/> is less than -1.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">
        /// The provided <paramref name="cancellationToken"/> has already been disposed.
        /// </exception>        
        /// <remarks>
        /// If the cancellation token is signaled before the specified time delay, then the Task is completed in
        /// Canceled state.  Otherwise, the Task is completed in RanToCompletion state once the specified time
        /// delay has expired.
        /// </remarks>        
        public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken)
        {
            // Throw on non-sensical time
            if (millisecondsDelay < -1)
            {
                throw new ArgumentOutOfRangeException("millisecondsDelay", Environment.GetResourceString("Task_Delay_InvalidMillisecondsDelay"));
            }
            Contract.EndContractBlock();

            // some short-cuts in case quick completion is in order
            if (cancellationToken.IsCancellationRequested)
            {
                // return a Task created as already-Canceled
                return Task.FromCancellation(cancellationToken);
            }
            else if (millisecondsDelay == 0)
            {
                // return a Task created as already-RanToCompletion
                return Task.CompletedTask;
            }

            // Construct a promise-style Task to encapsulate our return value
            var promise = new DelayPromise(cancellationToken);

            // Register our cancellation token, if necessary.
            if (cancellationToken.CanBeCanceled)
            {
                promise.Registration = cancellationToken.InternalRegisterWithoutEC(state => ((DelayPromise)state).Complete(), promise);
            }

            // ... and create our timer and make sure that it stays rooted.
            if (millisecondsDelay != Timeout.Infinite) // no need to create the timer if it's an infinite timeout
            {
                promise.Timer = new Timer(state => ((DelayPromise)state).Complete(), promise, millisecondsDelay, Timeout.Infinite);
                promise.Timer.KeepRootedWhileScheduled();
            }

            // Return the timer proxy task
            return promise;
        }

        /// <summary>Task that also stores the completion closure and logic for Task.Delay implementation.</summary>
        private sealed class DelayPromise : Task<VoidTaskResult>
        {
            internal DelayPromise(CancellationToken token)
                : base()
            {
                this.Token = token;
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCreation(CausalityTraceLevel.Required, this.Id, "Task.Delay", 0);

                if (Task.s_asyncDebuggingEnabled)
                {
                    AddToActiveTasks(this);
                }
            }

            internal readonly CancellationToken Token;
            internal CancellationTokenRegistration Registration;
            internal Timer Timer;

            internal void Complete()
            {
                // Transition the task to completed.
                bool setSucceeded;

                if (Token.IsCancellationRequested)
                {
                    setSucceeded = TrySetCanceled(Token);
                }
                else
                {
                    if (AsyncCausalityTracer.LoggingOn)
                        AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Completed);

                    if (Task.s_asyncDebuggingEnabled)
                    {
                        RemoveFromActiveTasks(this.Id);
                    }
                    setSucceeded = TrySetResult(default(VoidTaskResult));
                }

                // If we set the value, also clean up.
                if (setSucceeded)
                {
                    if (Timer != null) Timer.Dispose();
                    Registration.Dispose();
                }
            }
        }
        #endregion

        #region WhenAll
        /// <summary>
        /// Creates a task that will complete when all of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of all of the supplied tasks.</returns>
        /// <remarks>
        /// <para>
        /// If any of the supplied tasks completes in a faulted state, the returned task will also complete in a Faulted state, 
        /// where its exceptions will contain the aggregation of the set of unwrapped exceptions from each of the supplied tasks.  
        /// </para>
        /// <para>
        /// If none of the supplied tasks faulted but at least one of them was canceled, the returned task will end in the Canceled state.
        /// </para>
        /// <para>
        /// If none of the tasks faulted and none of the tasks were canceled, the resulting task will end in the RanToCompletion state.   
        /// </para>
        /// <para>
        /// If the supplied array/enumerable contains no tasks, the returned task will immediately transition to a RanToCompletion 
        /// state before it's returned to the caller.  
        /// </para>
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> collection contained a null task.
        /// </exception>
        public static Task WhenAll(IEnumerable<Task> tasks)
        {
            // Take a more efficient path if tasks is actually an array
            Task[] taskArray = tasks as Task[];
            if (taskArray != null)
            {
                return WhenAll(taskArray);
            }

            // Skip a List allocation/copy if tasks is a collection
            ICollection<Task> taskCollection = tasks as ICollection<Task>;
            if (taskCollection != null)
            {
                int index = 0;
                taskArray = new Task[taskCollection.Count];
                foreach (var task in tasks)
                {
                    if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                    taskArray[index++] = task;
                }
                return InternalWhenAll(taskArray);
            }

            // Do some argument checking and convert tasks to a List (and later an array).
            if (tasks == null) throw new ArgumentNullException("tasks");
            List<Task> taskList = new List<Task>();
            foreach (Task task in tasks)
            {
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                taskList.Add(task);
            }

            // Delegate the rest to InternalWhenAll()
            return InternalWhenAll(taskList.ToArray());
        }

        /// <summary>
        /// Creates a task that will complete when all of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of all of the supplied tasks.</returns>
        /// <remarks>
        /// <para>
        /// If any of the supplied tasks completes in a faulted state, the returned task will also complete in a Faulted state, 
        /// where its exceptions will contain the aggregation of the set of unwrapped exceptions from each of the supplied tasks.  
        /// </para>
        /// <para>
        /// If none of the supplied tasks faulted but at least one of them was canceled, the returned task will end in the Canceled state.
        /// </para>
        /// <para>
        /// If none of the tasks faulted and none of the tasks were canceled, the resulting task will end in the RanToCompletion state.   
        /// </para>
        /// <para>
        /// If the supplied array/enumerable contains no tasks, the returned task will immediately transition to a RanToCompletion 
        /// state before it's returned to the caller.  
        /// </para>
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> array contained a null task.
        /// </exception>
        public static Task WhenAll(params Task[] tasks)
        {
            // Do some argument checking and make a defensive copy of the tasks array
            if (tasks == null) throw new ArgumentNullException("tasks");
            Contract.EndContractBlock();

            int taskCount = tasks.Length;
            if (taskCount == 0) return InternalWhenAll(tasks); // Small optimization in the case of an empty array.

            Task[] tasksCopy = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                Task task = tasks[i];
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                tasksCopy[i] = task;
            }

            // The rest can be delegated to InternalWhenAll()
            return InternalWhenAll(tasksCopy);
        }

        // Some common logic to support WhenAll() methods
        // tasks should be a defensive copy.
        private static Task InternalWhenAll(Task[] tasks)
        {
            Contract.Requires(tasks != null, "Expected a non-null tasks array");
            return (tasks.Length == 0) ? // take shortcut if there are no tasks upon which to wait
                Task.CompletedTask :
                new WhenAllPromise(tasks);
        }

        // A Task<VoidTaskResult> that gets completed when all of its constituent tasks complete.
        // Completion logic will analyze the antecedents in order to choose completion status.
        // This type allows us to replace this logic:
        //      Task<VoidTaskResult> promise = new Task<VoidTaskResult>(...);
        //      Action<Task> completionAction = delegate { <completion logic>};
        //      TaskFactory.CommonCWAllLogic(tasksCopy).AddCompletionAction(completionAction);
        //      return promise;
        // which involves several allocations, with this logic:
        //      return new WhenAllPromise(tasksCopy);
        // which saves a couple of allocations and enables debugger notification specialization.
        //
        // Used in InternalWhenAll(Task[])
        private sealed class WhenAllPromise : Task<VoidTaskResult>, ITaskCompletionAction
        {
            /// <summary>
            /// Stores all of the constituent tasks.  Tasks clear themselves out of this
            /// array as they complete, but only if they don't have their wait notification bit set.
            /// </summary>
            private readonly Task[] m_tasks;
            /// <summary>The number of tasks remaining to complete.</summary>
            private int m_count;

            internal WhenAllPromise(Task[] tasks) :
                base()
            {
                Contract.Requires(tasks != null, "Expected a non-null task array");
                Contract.Requires(tasks.Length > 0, "Expected a non-zero length task array");

                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCreation(CausalityTraceLevel.Required, this.Id, "Task.WhenAll", 0);

                if (s_asyncDebuggingEnabled)
                {
                    AddToActiveTasks(this);
                }

                m_tasks = tasks;
                m_count = tasks.Length;

                foreach (var task in tasks)
                {
                    if (task.IsCompleted) this.Invoke(task); // short-circuit the completion action, if possible
                    else task.AddCompletionAction(this); // simple completion action
                }
            }

            public void Invoke(Task completedTask)
            {
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationRelation(CausalityTraceLevel.Important, this.Id, CausalityRelation.Join);

                // Decrement the count, and only continue to complete the promise if we're the last one.
                if (Interlocked.Decrement(ref m_count) == 0)
                {
                    // Set up some accounting variables
                    List<ExceptionDispatchInfo> observedExceptions = null;
                    Task canceledTask = null;

                    // Loop through antecedents:
                    //   If any one of them faults, the result will be faulted
                    //   If none fault, but at least one is canceled, the result will be canceled
                    //   If none fault or are canceled, then result will be RanToCompletion
                    for (int i = 0; i < m_tasks.Length; i++)
                    {
                        var task = m_tasks[i];
                        Contract.Assert(task != null, "Constituent task in WhenAll should never be null");

                        if (task.IsFaulted)
                        {
                            if (observedExceptions == null) observedExceptions = new List<ExceptionDispatchInfo>();
                            observedExceptions.AddRange(task.GetExceptionDispatchInfos());
                        }
                        else if (task.IsCanceled)
                        {
                            if (canceledTask == null) canceledTask = task; // use the first task that's canceled
                        }

                        // Regardless of completion state, if the task has its debug bit set, transfer it to the
                        // WhenAll task.  We must do this before we complete the task.
                        if (task.IsWaitNotificationEnabled) this.SetNotificationForWaitCompletion(enabled: true);
                        else m_tasks[i] = null; // avoid holding onto tasks unnecessarily
                    }

                    if (observedExceptions != null)
                    {
                        Contract.Assert(observedExceptions.Count > 0, "Expected at least one exception");

                        //We don't need to TraceOperationCompleted here because TrySetException will call Finish and we'll log it there

                        TrySetException(observedExceptions);
                    }
                    else if (canceledTask != null)
                    {
                        TrySetCanceled(canceledTask.CancellationToken, canceledTask.GetCancellationExceptionDispatchInfo());
                    }
                    else
                    {
                        if (AsyncCausalityTracer.LoggingOn)
                            AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Completed);

                        if (Task.s_asyncDebuggingEnabled)
                        {
                            RemoveFromActiveTasks(this.Id);
                        }
                        TrySetResult(default(VoidTaskResult));
                    }
                }
                Contract.Assert(m_count >= 0, "Count should never go below 0");
            }

            public bool InvokeMayRunArbitraryCode { get { return true; } }

            /// <summary>
            /// Returns whether we should notify the debugger of a wait completion.  This returns 
            /// true iff at least one constituent task has its bit set.
            /// </summary>
            internal override bool ShouldNotifyDebuggerOfWaitCompletion
            {
                get
                {
                    return
                        base.ShouldNotifyDebuggerOfWaitCompletion &&
                        Task.AnyTaskRequiresNotifyDebuggerOfWaitCompletion(m_tasks);
                }
            }
        }

        /// <summary>
        /// Creates a task that will complete when all of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of all of the supplied tasks.</returns>
        /// <remarks>
        /// <para>
        /// If any of the supplied tasks completes in a faulted state, the returned task will also complete in a Faulted state, 
        /// where its exceptions will contain the aggregation of the set of unwrapped exceptions from each of the supplied tasks.  
        /// </para>
        /// <para>
        /// If none of the supplied tasks faulted but at least one of them was canceled, the returned task will end in the Canceled state.
        /// </para>
        /// <para>
        /// If none of the tasks faulted and none of the tasks were canceled, the resulting task will end in the RanToCompletion state.  
        /// The Result of the returned task will be set to an array containing all of the results of the 
        /// supplied tasks in the same order as they were provided (e.g. if the input tasks array contained t1, t2, t3, the output 
        /// task's Result will return an TResult[] where arr[0] == t1.Result, arr[1] == t2.Result, and arr[2] == t3.Result). 
        /// </para>
        /// <para>
        /// If the supplied array/enumerable contains no tasks, the returned task will immediately transition to a RanToCompletion 
        /// state before it's returned to the caller.  The returned TResult[] will be an array of 0 elements.
        /// </para>
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> collection contained a null task.
        /// </exception>       
        public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            // Take a more efficient route if tasks is actually an array
            Task<TResult>[] taskArray = tasks as Task<TResult>[];
            if (taskArray != null)
            {
                return WhenAll<TResult>(taskArray);
            }

            // Skip a List allocation/copy if tasks is a collection
            ICollection<Task<TResult>> taskCollection = tasks as ICollection<Task<TResult>>;
            if (taskCollection != null)
            {
                int index = 0;
                taskArray = new Task<TResult>[taskCollection.Count];
                foreach (var task in tasks)
                {
                    if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                    taskArray[index++] = task;
                }
                return InternalWhenAll<TResult>(taskArray);
            }

            // Do some argument checking and convert tasks into a List (later an array)
            if (tasks == null) throw new ArgumentNullException("tasks");
            List<Task<TResult>> taskList = new List<Task<TResult>>();
            foreach (Task<TResult> task in tasks)
            {
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                taskList.Add(task);
            }

            // Delegate the rest to InternalWhenAll<TResult>().
            return InternalWhenAll<TResult>(taskList.ToArray());
        }

        /// <summary>
        /// Creates a task that will complete when all of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of all of the supplied tasks.</returns>
        /// <remarks>
        /// <para>
        /// If any of the supplied tasks completes in a faulted state, the returned task will also complete in a Faulted state, 
        /// where its exceptions will contain the aggregation of the set of unwrapped exceptions from each of the supplied tasks.  
        /// </para>
        /// <para>
        /// If none of the supplied tasks faulted but at least one of them was canceled, the returned task will end in the Canceled state.
        /// </para>
        /// <para>
        /// If none of the tasks faulted and none of the tasks were canceled, the resulting task will end in the RanToCompletion state.  
        /// The Result of the returned task will be set to an array containing all of the results of the 
        /// supplied tasks in the same order as they were provided (e.g. if the input tasks array contained t1, t2, t3, the output 
        /// task's Result will return an TResult[] where arr[0] == t1.Result, arr[1] == t2.Result, and arr[2] == t3.Result). 
        /// </para>
        /// <para>
        /// If the supplied array/enumerable contains no tasks, the returned task will immediately transition to a RanToCompletion 
        /// state before it's returned to the caller.  The returned TResult[] will be an array of 0 elements.
        /// </para>
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> array contained a null task.
        /// </exception>
        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks)
        {
            // Do some argument checking and make a defensive copy of the tasks array
            if (tasks == null) throw new ArgumentNullException("tasks");
            Contract.EndContractBlock();

            int taskCount = tasks.Length;
            if (taskCount == 0) return InternalWhenAll<TResult>(tasks); // small optimization in the case of an empty task array

            Task<TResult>[] tasksCopy = new Task<TResult>[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                Task<TResult> task = tasks[i];
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                tasksCopy[i] = task;
            }

            // Delegate the rest to InternalWhenAll<TResult>()
            return InternalWhenAll<TResult>(tasksCopy);
        }

        // Some common logic to support WhenAll<TResult> methods
        private static Task<TResult[]> InternalWhenAll<TResult>(Task<TResult>[] tasks)
        {
            Contract.Requires(tasks != null, "Expected a non-null tasks array");
            return (tasks.Length == 0) ? // take shortcut if there are no tasks upon which to wait
                new Task<TResult[]>(false, new TResult[0], TaskCreationOptions.None, default(CancellationToken)) :
                new WhenAllPromise<TResult>(tasks);
        }

        // A Task<T> that gets completed when all of its constituent tasks complete.
        // Completion logic will analyze the antecedents in order to choose completion status.
        // See comments for non-generic version of WhenAllPromise class.
        //
        // Used in InternalWhenAll<TResult>(Task<TResult>[])
        private sealed class WhenAllPromise<T> : Task<T[]>, ITaskCompletionAction
        {
            /// <summary>
            /// Stores all of the constituent tasks.  Tasks clear themselves out of this
            /// array as they complete, but only if they don't have their wait notification bit set.
            /// </summary>
            private readonly Task<T>[] m_tasks;
            /// <summary>The number of tasks remaining to complete.</summary>
            private int m_count;

            internal WhenAllPromise(Task<T>[] tasks) :
                base()
            {
                Contract.Requires(tasks != null, "Expected a non-null task array");
                Contract.Requires(tasks.Length > 0, "Expected a non-zero length task array");

                m_tasks = tasks;
                m_count = tasks.Length;

                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationCreation(CausalityTraceLevel.Required, this.Id, "Task.WhenAll", 0);

                if (s_asyncDebuggingEnabled)
                {
                    AddToActiveTasks(this);
                }

                foreach (var task in tasks)
                {
                    if (task.IsCompleted) this.Invoke(task); // short-circuit the completion action, if possible
                    else task.AddCompletionAction(this); // simple completion action
                }
            }

            public void Invoke(Task ignored)
            {
                if (AsyncCausalityTracer.LoggingOn)
                    AsyncCausalityTracer.TraceOperationRelation(CausalityTraceLevel.Important, this.Id, CausalityRelation.Join);

                // Decrement the count, and only continue to complete the promise if we're the last one.
                if (Interlocked.Decrement(ref m_count) == 0)
                {
                    // Set up some accounting variables
                    T[] results = new T[m_tasks.Length];
                    List<ExceptionDispatchInfo> observedExceptions = null;
                    Task canceledTask = null;

                    // Loop through antecedents:
                    //   If any one of them faults, the result will be faulted
                    //   If none fault, but at least one is canceled, the result will be canceled
                    //   If none fault or are canceled, then result will be RanToCompletion
                    for (int i = 0; i < m_tasks.Length; i++)
                    {
                        Task<T> task = m_tasks[i];
                        Contract.Assert(task != null, "Constituent task in WhenAll should never be null");

                        if (task.IsFaulted)
                        {
                            if (observedExceptions == null) observedExceptions = new List<ExceptionDispatchInfo>();
                            observedExceptions.AddRange(task.GetExceptionDispatchInfos());
                        }
                        else if (task.IsCanceled)
                        {
                            if (canceledTask == null) canceledTask = task; // use the first task that's canceled
                        }
                        else
                        {
                            Contract.Assert(task.Status == TaskStatus.RanToCompletion);
                            results[i] = task.GetResultCore(waitCompletionNotification: false); // avoid Result, which would triggering debug notification
                        }

                        // Regardless of completion state, if the task has its debug bit set, transfer it to the
                        // WhenAll task.  We must do this before we complete the task.
                        if (task.IsWaitNotificationEnabled) this.SetNotificationForWaitCompletion(enabled: true);
                        else m_tasks[i] = null; // avoid holding onto tasks unnecessarily
                    }

                    if (observedExceptions != null)
                    {
                        Contract.Assert(observedExceptions.Count > 0, "Expected at least one exception");

                        //We don't need to TraceOperationCompleted here because TrySetException will call Finish and we'll log it there

                        TrySetException(observedExceptions);
                    }
                    else if (canceledTask != null)
                    {
                        TrySetCanceled(canceledTask.CancellationToken, canceledTask.GetCancellationExceptionDispatchInfo());
                    }
                    else
                    {
                        if (AsyncCausalityTracer.LoggingOn)
                            AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Completed);

                        if (Task.s_asyncDebuggingEnabled)
                        {
                            RemoveFromActiveTasks(this.Id);
                        }
                        TrySetResult(results);
                    }
                }
                Contract.Assert(m_count >= 0, "Count should never go below 0");
            }

            public bool InvokeMayRunArbitraryCode { get { return true; } }

            /// <summary>
            /// Returns whether we should notify the debugger of a wait completion.  This returns true
            /// iff at least one constituent task has its bit set.
            /// </summary>
            internal override bool ShouldNotifyDebuggerOfWaitCompletion
            {
                get
                {
                    return
                        base.ShouldNotifyDebuggerOfWaitCompletion &&
                        Task.AnyTaskRequiresNotifyDebuggerOfWaitCompletion(m_tasks);
                }
            }
        }
        #endregion

        #region WhenAny
        /// <summary>
        /// Creates a task that will complete when any of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of one of the supplied tasks.  The return Task's Result is the task that completed.</returns>
        /// <remarks>
        /// The returned task will complete when any of the supplied tasks has completed.  The returned task will always end in the RanToCompletion state 
        /// with its Result set to the first task to complete.  This is true even if the first task to complete ended in the Canceled or Faulted state.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> array contained a null task, or was empty.
        /// </exception>
        public static Task<Task> WhenAny(params Task[] tasks)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            if (tasks.Length == 0)
            {
                throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_EmptyTaskList"), "tasks");
            }
            Contract.EndContractBlock();

            // Make a defensive copy, as the user may manipulate the tasks array
            // after we return but before the WhenAny asynchronously completes.
            int taskCount = tasks.Length;
            Task[] tasksCopy = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                Task task = tasks[i];
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                tasksCopy[i] = task;
            }

            // Previously implemented CommonCWAnyLogic() can handle the rest
            return TaskFactory.CommonCWAnyLogic(tasksCopy);
        }

        /// <summary>
        /// Creates a task that will complete when any of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of one of the supplied tasks.  The return Task's Result is the task that completed.</returns>
        /// <remarks>
        /// The returned task will complete when any of the supplied tasks has completed.  The returned task will always end in the RanToCompletion state 
        /// with its Result set to the first task to complete.  This is true even if the first task to complete ended in the Canceled or Faulted state.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> collection contained a null task, or was empty.
        /// </exception>
        public static Task<Task> WhenAny(IEnumerable<Task> tasks)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            Contract.EndContractBlock();

            // Make a defensive copy, as the user may manipulate the tasks collection
            // after we return but before the WhenAny asynchronously completes.
            List<Task> taskList = new List<Task>();
            foreach (Task task in tasks)
            {
                if (task == null) throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_NullTask"), "tasks");
                taskList.Add(task);
            }

            if (taskList.Count == 0)
            {
                throw new ArgumentException(Environment.GetResourceString("Task_MultiTaskContinuation_EmptyTaskList"), "tasks");
            }

            // Previously implemented CommonCWAnyLogic() can handle the rest
            return TaskFactory.CommonCWAnyLogic(taskList);
        }

        /// <summary>
        /// Creates a task that will complete when any of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of one of the supplied tasks.  The return Task's Result is the task that completed.</returns>
        /// <remarks>
        /// The returned task will complete when any of the supplied tasks has completed.  The returned task will always end in the RanToCompletion state 
        /// with its Result set to the first task to complete.  This is true even if the first task to complete ended in the Canceled or Faulted state.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> array contained a null task, or was empty.
        /// </exception>
        public static Task<Task<TResult>> WhenAny<TResult>(params Task<TResult>[] tasks)
        {
            // We would just like to do this:
            //    return (Task<Task<TResult>>) WhenAny( (Task[]) tasks);
            // but classes are not covariant to enable casting Task<TResult> to Task<Task<TResult>>.

            // Call WhenAny(Task[]) for basic functionality
            Task<Task> intermediate = WhenAny((Task[])tasks);

            // Return a continuation task with the correct result type
            return intermediate.ContinueWith(Task<TResult>.TaskWhenAnyCast, default(CancellationToken),
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// <summary>
        /// Creates a task that will complete when any of the supplied tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait on for completion.</param>
        /// <returns>A task that represents the completion of one of the supplied tasks.  The return Task's Result is the task that completed.</returns>
        /// <remarks>
        /// The returned task will complete when any of the supplied tasks has completed.  The returned task will always end in the RanToCompletion state 
        /// with its Result set to the first task to complete.  This is true even if the first task to complete ended in the Canceled or Faulted state.
        /// </remarks>
        /// <exception cref="T:System.ArgumentNullException">
        /// The <paramref name="tasks"/> argument was null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The <paramref name="tasks"/> collection contained a null task, or was empty.
        /// </exception>
        public static Task<Task<TResult>> WhenAny<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            // We would just like to do this:
            //    return (Task<Task<TResult>>) WhenAny( (IEnumerable<Task>) tasks);
            // but classes are not covariant to enable casting Task<TResult> to Task<Task<TResult>>.

            // Call WhenAny(IEnumerable<Task>) for basic functionality
            Task<Task> intermediate = WhenAny((IEnumerable<Task>)tasks);

            // Return a continuation task with the correct result type
            return intermediate.ContinueWith(Task<TResult>.TaskWhenAnyCast, default(CancellationToken),
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        #endregion

        [FriendAccessAllowed]
        internal static Task<TResult> CreateUnwrapPromise<TResult>(Task outerTask, bool lookForOce)
        {
            Contract.Requires(outerTask != null);

            return new UnwrapPromise<TResult>(outerTask, lookForOce);
        }

        internal virtual Delegate[] GetDelegateContinuationsForDebugger()
        {
            //Avoid an infinite loop by making sure the continuation object is not a reference to istelf.
            if (this.m_continuationObject != this)
                return GetDelegatesFromContinuationObject(this.m_continuationObject);
            else
                return null;
        }

        internal static Delegate[] GetDelegatesFromContinuationObject(object continuationObject)
        {
            if (continuationObject != null)
            {
                Action singleAction = continuationObject as Action;
                if (singleAction != null)
                {
                    return new Delegate[] { AsyncMethodBuilderCore.TryGetStateMachineForDebugger(singleAction) };
                }

                TaskContinuation taskContinuation = continuationObject as TaskContinuation;
                if (taskContinuation != null)
                {
                    return taskContinuation.GetDelegateContinuationsForDebugger();
                }

                Task continuationTask = continuationObject as Task;
                if (continuationTask != null)
                {
                    Contract.Assert(continuationTask.m_action == null);
                    Delegate[] delegates = continuationTask.GetDelegateContinuationsForDebugger();
                    if (delegates != null)
                        return delegates;
                }

                //We need this ITaskCompletionAction after the Task because in the case of UnwrapPromise
                //the VS debugger is more interested in the continuation than the internal invoke()
                ITaskCompletionAction singleCompletionAction = continuationObject as ITaskCompletionAction;
                if (singleCompletionAction != null)
                {
                    return new Delegate[] { new Action<Task>(singleCompletionAction.Invoke) };
                }

                List<object> continuationList = continuationObject as List<object>;
                if (continuationList != null)
                {
                    List<Delegate> result = new List<Delegate>();
                    foreach (object obj in continuationList)
                    {
                        var innerDelegates = GetDelegatesFromContinuationObject(obj);
                        if (innerDelegates != null)
                        {
                            foreach (var del in innerDelegates)
                            {
                                if (del != null)
                                    result.Add(del);
                            }
                        }
                    }

                    return result.ToArray();
                }
            }

            return null;
        }

        private static Task GetActiveTaskFromId(int taskId)
        {
            Task task = null;
            s_currentActiveTasks.TryGetValue(taskId, out task);
            return task;
        }

        private static Task[] GetActiveTasks()
        {
            
            return new List<Task>(s_currentActiveTasks.Values).ToArray();
        }


    }

    internal sealed class CompletionActionInvoker : IThreadPoolWorkItem
    {
        private readonly ITaskCompletionAction m_action;
        private readonly Task m_completingTask;

        internal CompletionActionInvoker(ITaskCompletionAction action, Task completingTask)
        {
            m_action = action;
            m_completingTask = completingTask;
        }

        [SecurityCritical]
        void IThreadPoolWorkItem.ExecuteWorkItem()
        {
            m_action.Invoke(m_completingTask);
        }

        [SecurityCritical]
        void IThreadPoolWorkItem.MarkAborted(ThreadAbortException tae)
        {
            /* NOP */
        }
    }

    // Proxy class for better debugging experience
    internal class SystemThreadingTasks_TaskDebugView
    {
        private Task m_task;

        public SystemThreadingTasks_TaskDebugView(Task task)
        {
            m_task = task;
        }

        public object AsyncState { get { return m_task.AsyncState; } }
        public TaskCreationOptions CreationOptions { get { return m_task.CreationOptions; } }
        public Exception Exception { get { return m_task.Exception; } }
        public int Id { get { return m_task.Id; } }
        public bool CancellationPending { get { return (m_task.Status == TaskStatus.WaitingToRun) && m_task.CancellationToken.IsCancellationRequested; } }
        public TaskStatus Status { get { return m_task.Status; } }
    }

    // Special purpose derivation of Task that supports limited replication through
    // overriding the ShouldReplicate() method.  This is used by the Parallel.For/ForEach
    // methods.
    internal class ParallelForReplicatingTask : Task
    {
        // Member variables
        private int m_replicationDownCount; // downcounter to control replication

        //
        // Constructors
        //

        [MethodImplAttribute(MethodImplOptions.NoInlining)] // Methods containing StackCrawlMark local var have to be marked non-inlineable            
        internal ParallelForReplicatingTask(
            ParallelOptions parallelOptions, Action action, TaskCreationOptions creationOptions, InternalTaskOptions internalOptions)
            : base(action, null, Task.InternalCurrent, default(CancellationToken), creationOptions, internalOptions | InternalTaskOptions.SelfReplicating, null)
        {
            // Compute the down count based on scheduler/DOP info in parallelOptions.
            m_replicationDownCount = parallelOptions.EffectiveMaxConcurrencyLevel;

            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            PossiblyCaptureContext(ref stackMark);
        }


        // Controls degree of replication.  If downcounter is initialized to -1, then
        // replication will be allowed to "run wild".  Otherwise, this method decrements
        // the downcounter each time it is called, calling false when it is called with
        // a zero downcounter.  This method returning false effectively ends the replication
        // of the associated ParallelForReplicatingTask.
        internal override bool ShouldReplicate()
        {
            if (m_replicationDownCount == -1) return true; // "run wild"

            if (m_replicationDownCount > 0) // Decrement and return true if not called with 0 downcount
            {
                m_replicationDownCount--;
                return true;
            }

            return false; // We're done replicating
        }

        internal override Task CreateReplicaTask(Action<object> taskReplicaDelegate, Object stateObject, Task parentTask, TaskScheduler taskScheduler,
                                            TaskCreationOptions creationOptionsForReplica, InternalTaskOptions internalOptionsForReplica)
        {
            return new ParallelForReplicaTask(taskReplicaDelegate, stateObject, parentTask, taskScheduler,
                                            creationOptionsForReplica, internalOptionsForReplica);
        }


    }

    internal class ParallelForReplicaTask : Task
    {
        internal object m_stateForNextReplica;  // some replicas may quit prematurely, in which case they will use this variable
        // to save state they want to be picked up by the next replica queued to the same thread

        internal object m_stateFromPreviousReplica;  // some replicas may quit prematurely, in which case they will use this variable
        // to save state they want to be picked up by the next replica queued to the same thread

        internal Task m_handedOverChildReplica;  // some replicas may quit prematurely, in which case they will use this variable
        // to hand over the child replica they had queued to the next task that will replace them

        internal ParallelForReplicaTask(Action<object> taskReplicaDelegate, Object stateObject, Task parentTask, TaskScheduler taskScheduler,
                                       TaskCreationOptions creationOptionsForReplica, InternalTaskOptions internalOptionsForReplica) :
            base(taskReplicaDelegate, stateObject, parentTask, default(CancellationToken), creationOptionsForReplica, internalOptionsForReplica, taskScheduler)
        {
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to pass on state to the next replica
        internal override Object SavedStateForNextReplica
        {
            get { return m_stateForNextReplica; }

            set { m_stateForNextReplica = value; }
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to pass on state to the next replica
        internal override Object SavedStateFromPreviousReplica
        {
            get { return m_stateFromPreviousReplica; }

            set { m_stateFromPreviousReplica = value; }
        }

        // Allows internal deriving classes to support replicas that exit prematurely and want to hand over the child replica that they
        // had queued, so that the replacement replica can work with that child task instead of queuing up yet another one
        internal override Task HandedOverChildReplica
        {
            get { return m_handedOverChildReplica; }

            set { m_handedOverChildReplica = value; }
        }
    }

    /// <summary>
    /// Specifies flags that control optional behavior for the creation and execution of tasks.
    /// </summary>
    // NOTE: These options are a subset of TaskContinuationsOptions, thus before adding a flag check it is
    // not already in use.
    [Flags]
    [Serializable]
    public enum TaskCreationOptions
    {
        /// <summary>
        /// Specifies that the default behavior should be used.
        /// </summary>
        None = 0x0,

        /// <summary>
        /// A hint to a <see cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see> to schedule a
        /// task in as fair a manner as possible, meaning that tasks scheduled sooner will be more likely to
        /// be run sooner, and tasks scheduled later will be more likely to be run later.
        /// </summary>
        PreferFairness = 0x01,

        /// <summary>
        /// Specifies that a task will be a long-running, course-grained operation. It provides a hint to the
        /// <see cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see> that oversubscription may be
        /// warranted. 
        /// </summary>
        LongRunning = 0x02,

        /// <summary>
        /// Specifies that a task is attached to a parent in the task hierarchy.
        /// </summary>
        AttachedToParent = 0x04,

        /// <summary>
        /// Specifies that an InvalidOperationException will be thrown if an attempt is made to attach a child task to the created task.
        /// </summary>
        DenyChildAttach = 0x08,

        /// <summary>
        /// Prevents the ambient scheduler from being seen as the current scheduler in the created task.  This means that operations
        /// like StartNew or ContinueWith that are performed in the created task will see TaskScheduler.Default as the current scheduler.
        /// </summary>
        HideScheduler = 0x10,

        // 0x20 is already being used in TaskContinuationOptions

        /// <summary>
        /// Forces continuations added to the current task to be executed asynchronously.
        /// This option has precedence over TaskContinuationOptions.ExecuteSynchronously
        /// </summary>
        RunContinuationsAsynchronously = 0x40
    }


    /// <summary>
    /// Task creation flags which are only used internally.
    /// </summary>
    [Flags]
    [Serializable]
    internal enum InternalTaskOptions
    {
        /// <summary> Specifies "No internal task options" </summary>
        None,

        /// <summary>Used to filter out internal vs. public task creation options.</summary>
        InternalOptionsMask = 0x0000FF00,

        ChildReplica = 0x0100,
        ContinuationTask = 0x0200,
        PromiseTask = 0x0400,
        SelfReplicating = 0x0800,

        /// <summary>
        /// Store the presence of TaskContinuationOptions.LazyCancellation, since it does not directly
        /// translate into any TaskCreationOptions.
        /// </summary>
        LazyCancellation = 0x1000,

        /// <summary>Specifies that the task will be queued by the runtime before handing it over to the user. 
        /// This flag will be used to skip the cancellationtoken registration step, which is only meant for unstarted tasks.</summary>
        QueuedByRuntime = 0x2000,

        /// <summary>
        /// Denotes that Dispose should be a complete nop for a Task.  Used when constructing tasks that are meant to be cached/reused.
        /// </summary>
        DoNotDispose = 0x4000
    }

    /// <summary>
    /// Specifies flags that control optional behavior for the creation and execution of continuation tasks.
    /// </summary>
    [Flags]
    [Serializable]
    public enum TaskContinuationOptions
    {
        /// <summary>
        /// Default = "Continue on any, no task options, run asynchronously"
        /// Specifies that the default behavior should be used.  Continuations, by default, will
        /// be scheduled when the antecedent task completes, regardless of the task's final <see
        /// cref="System.Threading.Tasks.TaskStatus">TaskStatus</see>.
        /// </summary>
        None = 0,

        // These are identical to their meanings and values in TaskCreationOptions

        /// <summary>
        /// A hint to a <see cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see> to schedule a
        /// task in as fair a manner as possible, meaning that tasks scheduled sooner will be more likely to
        /// be run sooner, and tasks scheduled later will be more likely to be run later.
        /// </summary>
        PreferFairness = 0x01,

        /// <summary>
        /// Specifies that a task will be a long-running, course-grained operation.  It provides
        /// a hint to the <see cref="System.Threading.Tasks.TaskScheduler">TaskScheduler</see> that
        /// oversubscription may be warranted.
        /// </summary>
        LongRunning = 0x02,
        /// <summary>
        /// Specifies that a task is attached to a parent in the task hierarchy.
        /// </summary>
        AttachedToParent = 0x04,

        /// <summary>
        /// Specifies that an InvalidOperationException will be thrown if an attempt is made to attach a child task to the created task.
        /// </summary>
        DenyChildAttach = 0x08,
        /// <summary>
        /// Prevents the ambient scheduler from being seen as the current scheduler in the created task.  This means that operations
        /// like StartNew or ContinueWith that are performed in the created task will see TaskScheduler.Default as the current scheduler.
        /// </summary>
        HideScheduler = 0x10,

        /// <summary>
        /// In the case of continuation cancellation, prevents completion of the continuation until the antecedent has completed.
        /// </summary>
        LazyCancellation = 0x20,

        RunContinuationsAsynchronously = 0x40,

        // These are specific to continuations

        /// <summary>
        /// Specifies that the continuation task should not be scheduled if its antecedent ran to completion.
        /// This option is not valid for multi-task continuations.
        /// </summary>
        NotOnRanToCompletion = 0x10000,
        /// <summary>
        /// Specifies that the continuation task should not be scheduled if its antecedent threw an unhandled
        /// exception. This option is not valid for multi-task continuations.
        /// </summary>
        NotOnFaulted = 0x20000,
        /// <summary>
        /// Specifies that the continuation task should not be scheduled if its antecedent was canceled. This
        /// option is not valid for multi-task continuations.
        /// </summary>
        NotOnCanceled = 0x40000,
        /// <summary>
        /// Specifies that the continuation task should be scheduled only if its antecedent ran to
        /// completion. This option is not valid for multi-task continuations.
        /// </summary>
        OnlyOnRanToCompletion = NotOnFaulted | NotOnCanceled,
        /// <summary>
        /// Specifies that the continuation task should be scheduled only if its antecedent threw an
        /// unhandled exception. This option is not valid for multi-task continuations.
        /// </summary>
        OnlyOnFaulted = NotOnRanToCompletion | NotOnCanceled,
        /// <summary>
        /// Specifies that the continuation task should be scheduled only if its antecedent was canceled.
        /// This option is not valid for multi-task continuations.
        /// </summary>
        OnlyOnCanceled = NotOnRanToCompletion | NotOnFaulted,
        /// <summary>
        /// Specifies that the continuation task should be executed synchronously. With this option
        /// specified, the continuation will be run on the same thread that causes the antecedent task to
        /// transition into its final state. If the antecedent is already complete when the continuation is
        /// created, the continuation will run on the thread creating the continuation.  Only very
        /// short-running continuations should be executed synchronously.
        /// </summary>
        ExecuteSynchronously = 0x80000
    }

    /// <summary>
    /// Internal helper class to keep track of stack depth and decide whether we should inline or not.
    /// </summary>
    internal class StackGuard
    {
        // current thread's depth of nested inline task executions
        private int m_inliningDepth = 0;

        // For relatively small inlining depths we don't want to get into the business of stack probing etc.
        // This clearly leaves a window of opportunity for the user code to SO. However a piece of code
        // that can SO in 20 inlines on a typical 1MB stack size probably needs to be revisited anyway.
        private const int MAX_UNCHECKED_INLINING_DEPTH = 20;

#if !FEATURE_CORECLR

        private UInt64 m_lastKnownWatermark;
        private static int s_pageSize;

        // We are conservative here. We assume that the platform needs a whole 64KB to
        // respond to stack overflow. This means that for very small stacks (e.g. 128KB) 
        // we'll fail a lot of stack checks incorrectly.
        private const long STACK_RESERVED_SPACE = 4096 * 16;

#endif  // !FEATURE_CORECLR

        /// <summary>
        /// This method needs to be called before attempting inline execution on the current thread. 
        /// If false is returned, it means we are too close to the end of the stack and should give up inlining.
        /// Each call to TryBeginInliningScope() that returns true must be matched with a 
        /// call to EndInliningScope() regardless of whether inlining actually took place.
        /// </summary>
        [SecuritySafeCritical]
        internal bool TryBeginInliningScope()
        {
            // If we're still under the 'safe' limit we'll just skip the stack probe to save p/invoke calls
            if (m_inliningDepth < MAX_UNCHECKED_INLINING_DEPTH || CheckForSufficientStack())
            {
                m_inliningDepth++;
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// This needs to be called once for each previous successful TryBeginInliningScope() call after
        /// inlining related logic runs.
        /// </summary>
        internal void EndInliningScope()
        {
            m_inliningDepth--;
            Contract.Assert(m_inliningDepth >= 0, "Inlining depth count should never go negative.");

            // do the right thing just in case...
            if (m_inliningDepth < 0) m_inliningDepth = 0;
        }

        [SecurityCritical]
        private unsafe bool CheckForSufficientStack()
        {
#if FEATURE_CORECLR
            return RuntimeHelpers.TryEnsureSufficientExecutionStack();
#else
            // see if we already have the system page size info recorded
            int pageSize = s_pageSize;
            if (pageSize == 0)
            {
                // If not we need to query it from GetSystemInfo()
                // Note that this happens only once for the process lifetime
                Win32Native.SYSTEM_INFO sysInfo = new Win32Native.SYSTEM_INFO();
                Win32Native.GetSystemInfo(ref sysInfo);

                s_pageSize = pageSize = sysInfo.dwPageSize;
            }

            Win32Native.MEMORY_BASIC_INFORMATION stackInfo = new Win32Native.MEMORY_BASIC_INFORMATION();

            // We subtract one page for our request. VirtualQuery rounds UP to the next page.
            // Unfortunately, the stack grows down. If we're on the first page (last page in the
            // VirtualAlloc), we'll be moved to the next page, which is off the stack! 

            UIntPtr currentAddr = new UIntPtr(&stackInfo - pageSize);
            UInt64 current64 = currentAddr.ToUInt64();

            // Check whether we previously recorded a deeper stack than where we currently are,
            // If so we don't need to do the P/Invoke to VirtualQuery
            if (m_lastKnownWatermark != 0 && current64 > m_lastKnownWatermark)
                return true;

            // Actual stack probe. P/Invoke to query for the current stack allocation information.            
            Win32Native.VirtualQuery(currentAddr.ToPointer(), ref stackInfo, (UIntPtr)(sizeof(Win32Native.MEMORY_BASIC_INFORMATION)));

            // If the current address minus the base (remember: the stack grows downward in the
            // address space) is greater than the number of bytes requested plus the reserved
            // space at the end, the request has succeeded.

            if ((current64 - ((UIntPtr)stackInfo.AllocationBase).ToUInt64()) > STACK_RESERVED_SPACE)
            {
                m_lastKnownWatermark = current64;
                return true;
            }

            return false;
#endif
        }
    }

    // Special internal struct that we use to signify that we are not interested in
    // a Task<VoidTaskResult>'s result.
    internal struct VoidTaskResult { }

    // Interface to which all completion actions must conform.
    // This interface allows us to combine functionality and reduce allocations.
    // For example, see Task.SetOnInvokeMres, and its use in Task.SpinThenBlockingWait().
    // This code:
    //      ManualResetEvent mres = new ManualResetEventSlim(false, 0);
    //      Action<Task> completionAction = delegate { mres.Set() ; };
    //      AddCompletionAction(completionAction);
    // gets replaced with this:
    //      SetOnInvokeMres mres = new SetOnInvokeMres();
    //      AddCompletionAction(mres);
    // For additional examples of where this is used, see internal classes Task.SignalOnInvokeCDE,
    // Task.WhenAllPromise, Task.WhenAllPromise<T>, TaskFactory.CompleteOnCountdownPromise,
    // TaskFactory.CompleteOnCountdownPromise<T>, and TaskFactory.CompleteOnInvokePromise.
    internal interface ITaskCompletionAction
    {
        /// <summary>Invoked to run the completion action.</summary>
        void Invoke(Task completingTask);

        /// <summary>
        /// Some completion actions are considered internal implementation details of tasks,
        /// using the continuation mechanism only for performance reasons.  Such actions perform
        /// known quantities and types of work, and can be invoked safely as a continuation even
        /// if the system wants to prevent arbitrary continuations from running synchronously.
        /// This should only return false for a limited set of implementations where a small amount
        /// of work is guaranteed to be performed, e.g. setting a ManualResetEventSlim.
        /// </summary>
        bool InvokeMayRunArbitraryCode { get; }
    }

    // This class encapsulates all "unwrap" logic, and also implements ITaskCompletionAction,
    // which minimizes the allocations needed for queuing it to its antecedent.  This
    // logic is used by both the Unwrap extension methods and the unwrap-style Task.Run methods.
    internal sealed class UnwrapPromise<TResult> : Task<TResult>, ITaskCompletionAction
    {
        // The possible states for our UnwrapPromise, used by Invoke() to determine which logic to execute
        private const byte STATE_WAITING_ON_OUTER_TASK = 0; // Invoke() means "process completed outer task"
        private const byte STATE_WAITING_ON_INNER_TASK = 1; // Invoke() means "process completed inner task"
        private const byte STATE_DONE = 2;                  // Invoke() means "something went wrong and we are hosed!"

        // Keep track of our state; initialized to STATE_WAITING_ON_OUTER_TASK in the constructor
        private byte _state;

        // "Should we check for OperationCanceledExceptions on the outer task and interpret them as proxy cancellation?"
        // Unwrap() sets this to false, Run() sets it to true.
        private readonly bool _lookForOce;

        public UnwrapPromise(Task outerTask, bool lookForOce)
            : base((object)null, outerTask.CreationOptions & TaskCreationOptions.AttachedToParent)
        {
            Contract.Requires(outerTask != null, "Expected non-null outerTask");
            _lookForOce = lookForOce;
            _state = STATE_WAITING_ON_OUTER_TASK;

            if (AsyncCausalityTracer.LoggingOn)
                AsyncCausalityTracer.TraceOperationCreation(CausalityTraceLevel.Required, this.Id, "Task.Unwrap", 0);

            if (Task.s_asyncDebuggingEnabled)
            {
                AddToActiveTasks(this);
            }

            // Link ourselves to the outer task.
            // If the outer task has already completed, take the fast path
            // of immediately transferring its results or processing the inner task.
            if (outerTask.IsCompleted)
            {
                ProcessCompletedOuterTask(outerTask);
            }
            else // Otherwise, process its completion asynchronously.
            {
                outerTask.AddCompletionAction(this);
            }
        }

        // For ITaskCompletionAction 
        public void Invoke(Task completingTask)
        {
            // Check the current stack guard.  If we're ok to inline,
            // process the task, and reset the guard when we're done.
            var sg = Task.CurrentStackGuard;
            if (sg.TryBeginInliningScope())
            {
                try { InvokeCore(completingTask); }
                finally { sg.EndInliningScope(); }
            }
            // Otherwise, we're too deep on the stack, and
            // we shouldn't run the continuation chain here, so queue a work
            // item to call back here to Invoke asynchronously.
            else InvokeCoreAsync(completingTask);
        }

        /// <summary>
        /// Processes the completed task. InvokeCore could be called twice:
        /// once for the outer task, once for the inner task.
        /// </summary>
        /// <param name="completingTask">The completing outer or inner task.</param>
        private void InvokeCore(Task completingTask)
        {
            switch (_state)
            {
                case STATE_WAITING_ON_OUTER_TASK:
                    ProcessCompletedOuterTask(completingTask);
                    // We bump the state inside of ProcessCompletedOuterTask because it can also be called from the constructor.
                    break;
                case STATE_WAITING_ON_INNER_TASK:
                    bool result = TrySetFromTask(completingTask, lookForOce: false);
                    _state = STATE_DONE; // bump the state
                    Contract.Assert(result, "Expected TrySetFromTask from inner task to succeed");
                    break;
                default:
                    Contract.Assert(false, "UnwrapPromise in illegal state");
                    break;
            }
        }

        // Calls InvokeCore asynchronously.
        [SecuritySafeCritical]
        private void InvokeCoreAsync(Task completingTask)
        {
            // Queue a call to Invoke.  If we're so deep on the stack that we're at risk of overflowing,
            // there's a high liklihood this thread is going to be doing lots more work before
            // returning to the thread pool (at the very least unwinding through thousands of
            // stack frames).  So we queue to the global queue.
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                // InvokeCore(completingTask);
                var tuple = (Tuple<UnwrapPromise<TResult>, Task>)state;
                tuple.Item1.InvokeCore(tuple.Item2);
            }, Tuple.Create<UnwrapPromise<TResult>, Task>(this, completingTask));
        }

        /// <summary>Processes the outer task once it's completed.</summary>
        /// <param name="task">The now-completed outer task.</param>
        private void ProcessCompletedOuterTask(Task task)
        {
            Contract.Requires(task != null && task.IsCompleted, "Expected non-null, completed outer task");
            Contract.Assert(_state == STATE_WAITING_ON_OUTER_TASK, "We're in the wrong state!");

            // Bump our state before proceeding any further
            _state = STATE_WAITING_ON_INNER_TASK;

            switch (task.Status)
            {
                // If the outer task did not complete successfully, then record the 
                // cancellation/fault information to tcs.Task.
                case TaskStatus.Canceled:
                case TaskStatus.Faulted:
                    bool result = TrySetFromTask(task, _lookForOce);
                    Contract.Assert(result, "Expected TrySetFromTask from outer task to succeed");
                    break;

                // Otherwise, process the inner task it returned.
                case TaskStatus.RanToCompletion:
                    var taskOfTaskOfTResult = task as Task<Task<TResult>>; // it's either a Task<Task> or Task<Task<TResult>>
                    ProcessInnerTask(taskOfTaskOfTResult != null ?
                        taskOfTaskOfTResult.Result : ((Task<Task>)task).Result);
                    break;
            }
        }

        /// <summary>Transfer the completion status from "task" to ourself.</summary>
        /// <param name="task">The source task whose results should be transfered to <paramref name="promise"/>.</param>
        /// <param name="lookForOce">Whether or not to look for OperationCanceledExceptions in task's exceptions if it faults.</param>
        /// <returns>true if the transfer was successful; otherwise, false.</returns>
        private bool TrySetFromTask(Task task, bool lookForOce)
        {
            Contract.Requires(task != null && task.IsCompleted, "TrySetFromTask: Expected task to have completed.");

            if (AsyncCausalityTracer.LoggingOn)
                AsyncCausalityTracer.TraceOperationRelation(CausalityTraceLevel.Important, this.Id, CausalityRelation.Join);

            bool result = false;
            switch (task.Status)
            {
                case TaskStatus.Canceled:
                    result = TrySetCanceled(task.CancellationToken, task.GetCancellationExceptionDispatchInfo());
                    break;

                case TaskStatus.Faulted:
                    var edis = task.GetExceptionDispatchInfos();
                    ExceptionDispatchInfo oceEdi;
                    OperationCanceledException oce;
                    if (lookForOce && edis.Count > 0 &&
                        (oceEdi = edis[0]) != null &&
                        (oce = oceEdi.SourceException as OperationCanceledException) != null)
                    {
                        result = TrySetCanceled(oce.CancellationToken, oceEdi);
                    }
                    else
                    {
                        result = TrySetException(edis);
                    }
                    break;

                case TaskStatus.RanToCompletion:
                    var taskTResult = task as Task<TResult>;

                    if (AsyncCausalityTracer.LoggingOn)
                        AsyncCausalityTracer.TraceOperationCompletion(CausalityTraceLevel.Required, this.Id, AsyncCausalityStatus.Completed);

                    if (Task.s_asyncDebuggingEnabled)
                    {
                        RemoveFromActiveTasks(this.Id);
                    }

                    result = TrySetResult(taskTResult != null ? taskTResult.Result : default(TResult));
                    break;
            }
            return result;
        }

        /// <summary>
        /// Processes the inner task of a Task{Task} or Task{Task{TResult}}, 
        /// transferring the appropriate results to ourself.
        /// </summary>
        /// <param name="task">The inner task returned by the task provided by the user.</param>
        private void ProcessInnerTask(Task task)
        {
            // If the inner task is null, the proxy should be canceled.
            if (task == null)
            {
                TrySetCanceled(default(CancellationToken));
                _state = STATE_DONE; // ... and record that we are done
            }

            // Fast path for if the inner task is already completed
            else if (task.IsCompleted)
            {
                TrySetFromTask(task, lookForOce: false);
                _state = STATE_DONE; // ... and record that we are done
            }

            // The inner task exists but is not yet complete, so when it does complete,
            // take some action to set our completion state.
            else
            {
                task.AddCompletionAction(this);
                // We'll record that we are done when Invoke() is called.
            }
        }

        public bool InvokeMayRunArbitraryCode { get { return true; } }
    }

}
