﻿// Copyright (c) Service Stack LLC. All Rights Reserved.
// License: https://raw.github.com/ServiceStack/ServiceStack/master/license.txt


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Web;
using Funq;
using ServiceStack.Caching;
using ServiceStack.Configuration;
using ServiceStack.Formats;
using ServiceStack.Host;
using ServiceStack.Host.Handlers;
using ServiceStack.Html;
using ServiceStack.IO;
using ServiceStack.Logging;
using ServiceStack.Messaging;
using ServiceStack.Metadata;
using ServiceStack.MiniProfiler.UI;
using ServiceStack.Serialization;
using ServiceStack.VirtualPath;
using ServiceStack.Web;

namespace ServiceStack
{
    public abstract partial class ServiceStackHost
        : IAppHost, IFunqlet, IHasContainer, IDisposable
    {
        private readonly ILog Log = LogManager.GetLogger(typeof(ServiceStackHost));

        public static ServiceStackHost Instance { get; protected set; }

        public DateTime StartedAt { get; set; }
        public DateTime AfterInitAt { get; set; }
        public DateTime ReadyAt { get; set; }

        protected ServiceStackHost(string serviceName, params Assembly[] assembliesWithServices)
        {
            this.StartedAt = DateTime.UtcNow;

            ServiceName = serviceName;
            Container = new Container { DefaultOwner = Owner.External };
            ServiceController = CreateServiceController(assembliesWithServices);

            ContentTypes = Host.ContentTypes.Instance;
            RestPaths = new List<RestPath>();
            Routes = new ServiceRoutes(this);
            Metadata = new ServiceMetadata(RestPaths);
            PreRequestFilters = new List<Action<IRequest, IResponse>>();
            GlobalRequestFilters = new List<Action<IRequest, IResponse, object>>();
            GlobalResponseFilters = new List<Action<IRequest, IResponse, object>>();
            ViewEngines = new List<IViewEngine>();
            ServiceExceptionHandlers = new List<HandleServiceExceptionDelegate>();
            UncaughtExceptionHandlers = new List<HandleUncaughtExceptionDelegate>();
            RawHttpHandlers = new List<Func<IHttpRequest, IHttpHandler>> {
                 HttpHandlerFactory.ReturnRequestInfo,
                 MiniProfilerHandler.MatchesRequest,
            };
            CatchAllHandlers = new List<HttpHandlerResolverDelegate>();
            CustomErrorHttpHandlers = new Dictionary<HttpStatusCode, IServiceStackHandler>();
            Plugins = new List<IPlugin> {
                new HtmlFormat(),
                new CsvFormat(),
                new MarkdownFormat(),
                new PredefinedRoutesFeature(),
                new MetadataFeature(),
            };
        }

        public abstract void Configure(Container container);

        protected virtual ServiceController CreateServiceController(params Assembly[] assembliesWithServices)
        {
            return new ServiceController(this, assembliesWithServices);
            //Alternative way to inject Service Resolver strategy
            //return new ServiceManager(this, 
            //    new ServiceController(() => assembliesWithServices.ToList().SelectMany(x => x.GetTypes())));
        }

        public virtual void SetConfig(HostConfig config)
        {
            Config = config;
        }

        public virtual ServiceStackHost Init()
        {
            if (Instance != null)
            {
                throw new InvalidDataException("ServiceStackHost.Instance has already been set");
            }
            Service.GlobalResolver = Instance = this;

            Config = HostConfig.ResetInstance();
            OnConfigLoad();

            Config.DebugMode = GetType().Assembly.IsDebugBuild();
            if (Config.DebugMode)
            {
                Plugins.Add(new RequestInfoFeature());
            }

            ServiceController.Init();
            Configure(Container);

            if (VirtualPathProvider == null)
            {
                var pathProviders = new List<IVirtualPathProvider> {
                    new FileSystemVirtualPathProvider(this, Config.WebHostPhysicalPath)
                };
                pathProviders.AddRange(Config.EmbeddedResourceSources.Map(x => 
                    new ResourceVirtualPathProvider(this, x)));

                VirtualPathProvider = pathProviders.Count > 1
                    ? new MultiVirtualPathProvider(this, pathProviders.ToArray())
                    : pathProviders.First();
            }

            OnAfterInit();

            var elapsed = DateTime.UtcNow - this.StartedAt;
            Log.InfoFormat("Initializing Application took {0}ms", elapsed.TotalMilliseconds);

            return this;
        }

        public virtual ServiceStackHost Start(string listeningAtUrlBase)
        {
            throw new NotImplementedException("Start(listeningAtUrlBase) is not supported by this AppHost");
        }

        public string ServiceName { get; set; }

        public ServiceMetadata Metadata { get; set; }

        public ServiceController ServiceController { get; set; }

        /// <summary>
        /// The AppHost.Container. Note: it is not thread safe to register dependencies after AppStart.
        /// </summary>
        public Container Container { get; private set; }

        public IServiceRoutes Routes { get; set; }

        public List<RestPath> RestPaths = new List<RestPath>();

        public Dictionary<Type, Func<IRequest, object>> RequestBinders
        {
            get { return ServiceController.RequestTypeFactoryMap; }
        }

        public IContentTypes ContentTypes { get; set; }

        public List<Action<IRequest, IResponse>> PreRequestFilters { get; set; }

        public List<Action<IRequest, IResponse, object>> GlobalRequestFilters { get; set; }

        public List<Action<IRequest, IResponse, object>> GlobalResponseFilters { get; set; }

        public List<IViewEngine> ViewEngines { get; set; }

        public List<HandleServiceExceptionDelegate> ServiceExceptionHandlers { get; set; }

        public List<HandleUncaughtExceptionDelegate> UncaughtExceptionHandlers { get; set; }

        public List<Func<IHttpRequest, IHttpHandler>> RawHttpHandlers { get; set; }
        
        public List<HttpHandlerResolverDelegate> CatchAllHandlers { get; set; }

        public Dictionary<HttpStatusCode, IServiceStackHandler> CustomErrorHttpHandlers { get; set; }

        public List<IPlugin> Plugins { get; set; }

        public IVirtualPathProvider VirtualPathProvider { get; set; }

        /// <summary>
        /// Executed immediately before a Service is executed. Use return to change the request DTO used, must be of the same type.
        /// </summary>
        public virtual object OnPreExecuteServiceFilter(IService service, object request, IRequest httpReq, IResponse httpRes)
        {
            return request;
        }

        /// <summary>
        /// Executed immediately after a service is executed. Use return to change response used.
        /// </summary>
        public virtual object OnPostExecuteServiceFilter(IService service, object response, IRequest httpReq, IResponse httpRes)
        {
            return response;
        }

        /// <summary>
        /// Occurs when the Service throws an Exception.
        /// </summary>
        public virtual object OnServiceException(IRequest httpReq, object request, Exception ex)
        {
            object lastError = null;
            foreach (var errorHandler in ServiceExceptionHandlers)
            {
                lastError = errorHandler(httpReq, request, ex) ?? lastError;
            }
            return lastError;
        }

        /// <summary>
        /// Occurs when an exception is thrown whilst processing a request.
        /// </summary>
        public virtual void OnUncaughtException(IRequest httpReq, IResponse httpRes, string operationName, Exception ex)
        {
            if (UncaughtExceptionHandlers.Count > 0)
            {
                foreach (var errorHandler in UncaughtExceptionHandlers)
                {
                    errorHandler(httpReq, httpRes, operationName, ex);
                }
            }
            else
            {
                var errorMessage = string.Format("Error occured while Processing Request: {0}", ex.Message);
                var statusCode = ex.ToStatusCode();

                //httpRes.WriteToResponse always calls .Close in it's finally statement so 
                //if there is a problem writing to response, by now it will be closed
                if (!httpRes.IsClosed)
                {
                    httpRes.WriteErrorToResponse(httpReq, httpReq.ResponseContentType, operationName, errorMessage, ex, statusCode);
                }
            }
        }

        private HostConfig config;
        public HostConfig Config
        {
            get
            {
                return config;
            }
            set
            {
                config = value;
                OnAfterConfigChanged();
            }
        }

        public virtual void OnConfigLoad()
        {
        }

        // Config has changed
        public virtual void OnAfterConfigChanged()
        {
            config.ServiceEndpointsMetadataConfig = ServiceEndpointsMetadataConfig.Create(config.ServiceStackHandlerFactoryPath);

            JsonDataContractSerializer.Instance.UseBcl = config.UseBclJsonSerializers;
            JsonDataContractDeserializer.Instance.UseBcl = config.UseBclJsonSerializers;
        }

        //After configure called
        public void OnAfterInit()
        {
            AfterInitAt = DateTime.UtcNow;

            if (config.EnableFeatures != Feature.All)
            {
                if ((Feature.Xml & config.EnableFeatures) != Feature.Xml)
                    config.IgnoreFormatsInMetadata.Add("xml");
                if ((Feature.Json & config.EnableFeatures) != Feature.Json)
                    config.IgnoreFormatsInMetadata.Add("json");
                if ((Feature.Jsv & config.EnableFeatures) != Feature.Jsv)
                    config.IgnoreFormatsInMetadata.Add("jsv");
                if ((Feature.Csv & config.EnableFeatures) != Feature.Csv)
                    config.IgnoreFormatsInMetadata.Add("csv");
                if ((Feature.Html & config.EnableFeatures) != Feature.Html)
                    config.IgnoreFormatsInMetadata.Add("html");
                if ((Feature.Soap11 & config.EnableFeatures) != Feature.Soap11)
                    config.IgnoreFormatsInMetadata.Add("soap11");
                if ((Feature.Soap12 & config.EnableFeatures) != Feature.Soap12)
                    config.IgnoreFormatsInMetadata.Add("soap12");
            }

            if ((Feature.Html & config.EnableFeatures) != Feature.Html)
                Plugins.RemoveAll(x => x is HtmlFormat);

            if ((Feature.Csv & config.EnableFeatures) != Feature.Csv)
                Plugins.RemoveAll(x => x is CsvFormat);

            if ((Feature.Markdown & config.EnableFeatures) != Feature.Markdown)
                Plugins.RemoveAll(x => x is MarkdownFormat);

            if ((Feature.PredefinedRoutes & config.EnableFeatures) != Feature.PredefinedRoutes)
                Plugins.RemoveAll(x => x is PredefinedRoutesFeature);

            if ((Feature.Metadata & config.EnableFeatures) != Feature.Metadata)
                Plugins.RemoveAll(x => x is MetadataFeature);

            if ((Feature.RequestInfo & config.EnableFeatures) != Feature.RequestInfo)
                Plugins.RemoveAll(x => x is RequestInfoFeature);

            if ((Feature.Razor & config.EnableFeatures) != Feature.Razor)
                Plugins.RemoveAll(x => x is IRazorPlugin);    //external

            if ((Feature.ProtoBuf & config.EnableFeatures) != Feature.ProtoBuf)
                Plugins.RemoveAll(x => x is IProtoBufPlugin); //external

            if ((Feature.MsgPack & config.EnableFeatures) != Feature.MsgPack)
                Plugins.RemoveAll(x => x is IMsgPackPlugin);  //external

            if (config.ServiceStackHandlerFactoryPath != null)
                config.ServiceStackHandlerFactoryPath = config.ServiceStackHandlerFactoryPath.TrimStart('/');

            var specifiedContentType = config.DefaultContentType; //Before plugins loaded

            ConfigurePlugins();

            LoadPlugin(Plugins.ToArray());
            pluginsLoaded = true;

            AfterPluginsLoaded(specifiedContentType);

            var registeredCacheClient = TryResolve<ICacheClient>();
            using (registeredCacheClient)
            {
                if (registeredCacheClient == null)
                {
                    Container.Register<ICacheClient>(new MemoryCacheClient());
                }
            }

            var registeredMqService = TryResolve<IMessageService>();
            var registeredMqFactory = TryResolve<IMessageFactory>();
            if (registeredMqService != null && registeredMqFactory == null)
            {
                Container.Register(c => registeredMqService.MessageFactory);
            }

            ReadyAt = DateTime.UtcNow;
        }

        private void ConfigurePlugins()
        {
            //Some plugins need to initialize before other plugins are registered.

            foreach (var plugin in Plugins)
            {
                var preInitPlugin = plugin as IPreInitPlugin;
                if (preInitPlugin != null)
                {
                    preInitPlugin.Configure(this);
                }
            }
        }

        private void AfterPluginsLoaded(string specifiedContentType)
        {
            if (!String.IsNullOrEmpty(specifiedContentType))
                config.DefaultContentType = specifiedContentType;
            else if (String.IsNullOrEmpty(config.DefaultContentType))
                config.DefaultContentType = MimeTypes.Json;

            ServiceController.AfterInit();
        }

        private bool pluginsLoaded;
        public void AddPlugin(params IPlugin[] plugins)
        {
            if (pluginsLoaded)
            {
                LoadPlugin(plugins);
            }
            else
            {
                foreach (var plugin in plugins)
                {
                    Plugins.Add(plugin);
                }
            }
        }

        public virtual void Release(object instance)
        {
            try
            {
                var iocAdapterReleases = Container.Adapter as IRelease;
                if (iocAdapterReleases != null)
                {
                    iocAdapterReleases.Release(instance);
                }
                else
                {
                    var disposable = instance as IDisposable;
                    if (disposable != null)
                        disposable.Dispose();
                }
            }
            catch { /*ignore*/ }
        }

        public virtual void OnEndRequest()
        {
            foreach (var item in RequestContext.Instance.Items.Values)
            {
                Release(item);
            }

            RequestContext.Instance.EndRequest();
        }

        public virtual void Register<T>(T instance)
        {
            this.Container.Register(instance);
        }

        public virtual void RegisterAs<T, TAs>() where T : TAs
        {
            this.Container.RegisterAutoWiredAs<T, TAs>();
        }

        public virtual T TryResolve<T>()
        {
            return this.Container.TryResolve<T>();
        }

        public virtual T Resolve<T>()
        {
            return this.Container.Resolve<T>();
        }

        public virtual IServiceRunner<TRequest> CreateServiceRunner<TRequest>(ActionContext actionContext)
        {
            //cached per service action
            return new ServiceRunner<TRequest>(this, actionContext);
        }

        public virtual string ResolveLocalizedString(string text)
        {
            return text;
        }

        public virtual string ResolveAbsoluteUrl(string virtualPath, IRequest httpReq)
        {
            return httpReq.GetAbsoluteUrl(virtualPath); //Http Listener, TODO: ASP.NET overrides
        }

        public virtual string ResolvePhysicalPath(string virtualPath, IRequest httpReq)
        {
            return VirtualPathProvider.CombineVirtualPath(VirtualPathProvider.RootDirectory.RealPath, virtualPath);
        }

        public virtual IVirtualFile ResolveVirtualFile(string virtualPath, IRequest httpReq)
        {
            return VirtualPathProvider.GetFile(virtualPath);
        }

        public virtual IVirtualDirectory ResolveVirtualDirectory(string virtualPath, IRequest httpReq)
        {
            return virtualPath == VirtualPathProvider.VirtualPathSeparator
                ? VirtualPathProvider.RootDirectory
                : VirtualPathProvider.GetDirectory(virtualPath);
        }

        public virtual IVirtualNode ResolveVirtualNode(string virtualPath, IRequest httpReq)
        {
            return (IVirtualNode) ResolveVirtualFile(virtualPath, httpReq) 
                ?? ResolveVirtualDirectory(virtualPath, httpReq);
        }

        public virtual void LoadPlugin(params IPlugin[] plugins)
        {
            foreach (var plugin in plugins)
            {
                try
                {
                    plugin.Register(this);
                }
                catch (Exception ex)
                {
                    Log.Warn("Error loading plugin " + plugin.GetType().Name, ex);
                }
            }
        }

        public virtual object ExecuteService(object requestDto)
        {
            return ExecuteService(requestDto, RequestAttributes.None);
        }

        public virtual object ExecuteService(object requestDto, RequestAttributes requestAttributes)
        {
            return ServiceController.Execute(requestDto, new BasicRequest(requestDto, requestAttributes));
        }

        public virtual void RegisterService(Type serviceType, params string[] atRestPaths)
        {
            ServiceController.RegisterService(serviceType);
            var reqAttr = serviceType.FirstAttribute<DefaultRequestAttribute>();
            if (reqAttr != null)
            {
                foreach (var atRestPath in atRestPaths)
                {
                    this.Routes.Add(reqAttr.RequestType, atRestPath, null);
                }
            }
        }

        public virtual void Dispose()
        {
            if (Container != null)
            {
                Container.Dispose();
                Container = null;
            }

            Instance = null;
        }
    }
}