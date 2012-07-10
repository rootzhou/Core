#region Copyright
// Copyright (c) 2009 - 2012, Kazi Manzur Rashid <kazimanzurrashid@gmail.com>, 2011 - 2012 hazzik <hazzik@gmail.com>.
// This source is subject to the Microsoft Public License. 
// See http://www.microsoft.com/opensource/licenses.mspx#Ms-PL. 
// All other rights reserved.
#endregion

namespace MvcExtensions
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Web;
    using System.Web.Mvc;
    using System.Web.Routing;

    /// <summary>
    /// Defines a base class which is used to execute application startup and cleanup tasks.
    /// </summary>
    public abstract class Bootstrapper : Disposable, IBootstrapper
    {
        private readonly object syncLock = new object();

        private volatile ContainerAdapter container;

        /// <summary>
        /// Initializes a new instance of the <see cref="Bootstrapper"/> class.
        /// </summary>
        /// <param name="buildManager">The build manager.</param>
        /// <param name="bootstrapperTasks">The bootstrapper tasks.</param>
        /// <param name="perRequestTasks">The per request tasks.</param>
        protected Bootstrapper(IBuildManager buildManager, IBootstrapperTasksRegistry bootstrapperTasks, IPerRequestTasksRegistry perRequestTasks)
        {
            Invariant.IsNotNull(buildManager, "buildManager");
            Invariant.IsNotNull(bootstrapperTasks, "bootstrapperTasks");
            Invariant.IsNotNull(bootstrapperTasks, "perRequestTasks");

            BuildManager = buildManager;

            BuildManager = buildManager;
            BootstrapperTasks = bootstrapperTasks;
            PerRequestTasks = perRequestTasks;
        }

        /// <summary>
        /// Current bootstrapper
        /// </summary>
        public static IBootstrapper Current 
        { 
            get; 
            protected set; 
        }

        /// <summary>
        /// Gets  the build manager.
        /// </summary>
        /// <value>The build manager.</value>
        public IBuildManager BuildManager
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the bootstrapper task registry.
        /// </summary>
        /// <value>The bootstrapper tasks.</value>
        public IBootstrapperTasksRegistry BootstrapperTasks
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the per request task registry.
        /// </summary>
        /// <value>The per request tasks.</value>
        public IPerRequestTasksRegistry PerRequestTasks
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the container.
        /// </summary>
        /// <value>The container.</value>
        public ContainerAdapter Adapter
        {
            [DebuggerStepThrough]
            get
            {
                if (container == null)
                {
                    lock (syncLock)
                    {
                        if (container == null)
                        {
                            container = CreateAndSetCurrent();
                            LoadModules();
                        }
                    }
                }

                return container;
            }
        }

        /// <summary>
        /// Executes the <seealso cref="BootstrapperTask"/>.
        /// </summary>
        public void ExecuteBootstrapperTasks()
        {
            Execute<BootstrapperTask>(BootstrapperTasks.TaskConfigurations);
        }

        /// <summary>
        /// Dispose the <seealso cref="BootstrapperTask"/>.
        /// </summary>
        public void DisposeBootstrapperTasks()
        {
            Cleanup<BootstrapperTask>(BootstrapperTasks.TaskConfigurations);
        }

        /// <summary>
        /// Executes the <seealso cref="PerRequestTask"/>.
        /// </summary>
        public void ExecutePerRequestTasks()
        {
            Execute<PerRequestTask>(PerRequestTasks.TaskConfigurations);
        }

        /// <summary>
        /// Dispose the <seealso cref="PerRequestTask"/>.
        /// </summary>
        public void DisposePerRequestTasks()
        {
            Cleanup<PerRequestTask>(PerRequestTasks.TaskConfigurations);
        }

        /// <summary>
        /// Creates the container adapter.
        /// </summary>
        /// <returns></returns>
        protected abstract ContainerAdapter CreateAdapter();

        /// <summary>
        /// Loads the container specific modules.
        /// </summary>
        protected abstract void LoadModules();

        /// <summary>
        /// Disposes the resources.
        /// </summary>
        protected override void DisposeCore()
        {
            if (container == null)
            {
                return;
            }

            container.Dispose();
        }

        private void Execute<TTask>(IEnumerable<KeyValuePair<Type, Action<object>>> tasks) where TTask : Task
        {
            var shouldSkip = false;

            foreach (var taskConfiguration in tasks)
            {
                if (shouldSkip)
                {
                    shouldSkip = false;
                    continue;
                }

                var task = (TTask)Adapter.GetService(taskConfiguration.Key);

                Debug.Assert(task != null, "Task should be not null");

                if (taskConfiguration.Value != null)
                {
                    taskConfiguration.Value(task);
                }

                var continuation = task.Execute();

                if (continuation == TaskContinuation.Break)
                {
                    break;
                }

                shouldSkip = continuation == TaskContinuation.Skip;
            }
        }

        private void Cleanup<TTask>(IEnumerable<KeyValuePair<Type, Action<object>>> tasks) where TTask : Task
        {
            foreach (var task in tasks.Select(taskConfiguration => (TTask)Adapter.GetService(taskConfiguration.Key)))
            {
                task.Dispose();
            }
        }

        private void Register(ContainerAdapter adapter)
        {
            adapter.RegisterInstance(RouteTable.Routes)
                .RegisterInstance(BuildManager)
                .RegisterAsSingleton<IFilterRegistry, FilterRegistry>()
                .RegisterAsSingleton<IFilterProvider, FilterProvider>()
                .RegisterAsSingleton<IModelMetadataRegistry, ModelMetadataRegistry>();

            BuildManager.ConcreteTypes
                .Where(type => KnownTypes.BootstrapperTaskType.IsAssignableFrom(type))
                .Each(type => adapter.RegisterAsSingleton(type));

            BuildManager.ConcreteTypes
                .Where(type => KnownTypes.PerRequestTaskType.IsAssignableFrom(type))
                .Each(type => adapter.RegisterAsPerRequest(type));

            adapter.RegisterInstance<IServiceRegistrar>(adapter)
                .RegisterInstance<IDependencyResolver>(adapter)
                .RegisterInstance<IServiceInjector>(adapter)
                .RegisterInstance(adapter)
                .RegisterInstance(BootstrapperTasks)
                .RegisterInstance(PerRequestTasks)
                .RegisterInstance(new TypeMappingRegistry<Controller, IActionInvoker>())
                .RegisterInstance(new TypeMappingRegistry<Controller, IControllerActivator>())
                .RegisterInstance(new TypeMappingRegistry<IView, IViewPageActivator>())
                .RegisterInstance(new TypeMappingRegistry<object, IModelBinder>());
        }

        private ContainerAdapter CreateAndSetCurrent()
        {
            var adapter = CreateAdapter();

            Register(adapter);

            DependencyResolver.SetResolver(adapter);

            return adapter;
        }

        /// <summary>
        /// The bootstrapper module
        /// </summary>
        public class Module : IHttpModule
        {
            private static readonly object lockSync = new object();
            private static int initializeModuleCount;

            /// <summary>
            /// Initializes a module and prepares it to handle requests.
            /// </summary>
            /// <param name="context">An <see cref="T:System.Web.HttpApplication"/> that provides access to the methods, properties, and events common to all application objects within an ASP.NET application </param>
            public void Init(HttpApplication context)
            {
                lock (lockSync)
                {
                    if (initializeModuleCount++ == 0)
                    {
                        context.BeginRequest += (sender, args) => Current.ExecutePerRequestTasks();
                        context.EndRequest += (sender, args) => Current.DisposePerRequestTasks();
                        Current.ExecuteBootstrapperTasks();
                    }
                }
            }

            /// <summary>
            /// Disposes of the resources (other than memory) used by the module that implements <see cref="T:System.Web.IHttpModule"/>.
            /// </summary>
            public void Dispose()
            {
                lock (lockSync)
                {
                    if (--initializeModuleCount == 0)
                    {
                        Current.DisposeBootstrapperTasks();
                    }
                }
            }
        }
    }
}
