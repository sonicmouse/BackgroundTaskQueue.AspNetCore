using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks.Dataflow;

namespace BackgroundTaskQueue.AspNetCore
{
	/// <summary>
	/// Provides a way to offload background work in a fire-and-forget fashion,
	/// with optional support for categories and bounded concurrency.
	/// </summary>
	public interface IOffloadWorkService
	{
		/// <summary>
		/// Offloads a unit of work to the default background category for execution.
		/// </summary>
		/// <typeparam name="T">The type of the parameter passed to the work delegate.</typeparam>
		/// <param name="work">The delegate to execute. It receives the scoped service provider, the input parameter, and a cancellation token.</param>
		/// <param name="param">The parameter passed to the work delegate.</param>
		/// <returns>True if the work was accepted; false if it was rejected due to capacity limits or shutdown.</returns>
		bool Offload<T>(Func<IServiceProvider, T, CancellationToken, Task> work, T param);

		/// <summary>
		/// Offloads a unit of work to a specific named category for execution.
		/// </summary>
		/// <typeparam name="T">The type of the parameter passed to the work delegate.</typeparam>
		/// <param name="categoryName">The name of the category to route the work to.</param>
		/// <param name="work">The delegate to execute. It receives the scoped service provider, the input parameter, and a cancellation token.</param>
		/// <param name="param">The parameter passed to the work delegate.</param>
		/// <returns>True if the work was accepted; false if it was rejected due to capacity limits or shutdown.</returns>
		bool Offload<T>(string categoryName,
			Func<IServiceProvider, T, CancellationToken, Task> work, T param);

		/// <summary>
		/// Gets the number of currently active (running + queued) work items in the default category.
		/// </summary>
		/// <returns>The number of active items.</returns>
		long GetActiveCount();

		/// <summary>
		/// Gets the number of currently active (running + queued) work items in the specified category.
		/// </summary>
		/// <param name="categoryName">The name of the category.</param>
		/// <returns>The number of active items in the specified category.</returns>
		long GetActiveCount(string categoryName);
	}

#if NET7_0_OR_GREATER
	file
#else
	internal
#endif
	sealed class OffloadWorkService : BackgroundService, IOffloadWorkService
	{
		private const string UnhandledExceptionLogMsg = "Offloaded work threw an unhandled exception.";
		private const string DispatchLoopErrorLogMsg = "Error in dispatch loop.";
		private const string TransferErrorLogMsg = "Error transferring work item to worker block for category.";

		private readonly OffloadWorkServiceCategoryOptions _options;
		private readonly IOffloadWorkServiceState _state;
		private readonly IServiceScopeFactory _serviceScopeFactory;
		private readonly ILogger<OffloadWorkService> _logger;

		public OffloadWorkService(
			IOptions<OffloadWorkServiceCategoryOptions> options,
			IOffloadWorkServiceState state,
			IServiceScopeFactory serviceScopeFactory,
			ILogger<OffloadWorkService> logger)
		{
			_options = options.Value;
			_state = state;
			_serviceScopeFactory = serviceScopeFactory;
			_logger = logger;
		}

		public bool Offload<T>(Func<IServiceProvider, T, CancellationToken, Task> work, T param)
		{
			try
			{
				return Offload(OffloadWorkServiceExtensions.DefaultCategoryName, work, param);
			}
			catch (InvalidOperationException)
			{
				throw new InvalidOperationException(
					"Offload service is unavailable or not initialized correctly.");
			}
		}

		public bool Offload<T>(string categoryName,
			Func<IServiceProvider, T, CancellationToken, Task> work, T param)
		{
			if (_state.CategoryStates.TryGetValue(categoryName, out var catDesc))
			{
				lock (catDesc.OffloadLock)
				{
					if (catDesc.BoundedCapacity != -1 &&
						catDesc.SafeActiveCount >= catDesc.BoundedCapacity)
					{
						return false;
					}

					var serviceScope = _serviceScopeFactory.CreateAsyncScope();

					if (catDesc.StagingBlock.Post(() =>
					{
						return RunServiceScopedTask(serviceScope, work, param, _logger, catDesc);
					}))
					{
						Interlocked.Increment(ref catDesc.ActiveCount);
						return true;
					}

					serviceScope.Dispose();
					return false;
				}
			}

			throw new InvalidOperationException($"The category '{categoryName}' is not registered.");
		}

		public long GetActiveCount()
		{
			try
			{
				return GetActiveCount(OffloadWorkServiceExtensions.DefaultCategoryName);
			}
			catch (InvalidOperationException)
			{
				throw new InvalidOperationException(
					"Offload service is unavailable or not initialized correctly.");
			}
		}

		public long GetActiveCount(string categoryName)
		{
			if (_state.CategoryStates.TryGetValue(categoryName, out var catDesc))
			{
				return catDesc.SafeActiveCount;
			}

			throw new InvalidOperationException($"The category '{categoryName}' is not registered.");
		}

		private static async Task RunServiceScopedTask<T>(AsyncServiceScope asyncServiceScope,
			Func<IServiceProvider, T, CancellationToken, Task> work, T param, ILogger logger,
			CategoryState catDesc)
		{
			await using (asyncServiceScope)
			{
				var sp = asyncServiceScope.ServiceProvider;
				try
				{
					await work(sp, param, catDesc.CancellationTokenSource.Token);
				}
				catch (OperationCanceledException)
				{
					// service is shutting down
				}
				catch (Exception ex)
				{
					LogError(sp, ex, UnhandledExceptionLogMsg, catDesc.CategoryName, logger);
				}
				finally
				{
					Interlocked.Decrement(ref catDesc.ActiveCount);
				}
			}
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			foreach (var (categoryName, option) in _options.GetCategories())
			{
				var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

				var worker = new ActionBlock<Func<Task>>(t => t(), new()
				{
					MaxDegreeOfParallelism = option.MaxDegreeOfParallelism,
					CancellationToken = cts.Token
				});

				var staging = new BufferBlock<Func<Task>>(new()
				{
					CancellationToken = cts.Token
				});

				_state.CategoryStates.TryAdd(categoryName,
					new(categoryName, option.BoundedCapacity, worker, staging, cts));
			}

			return base.StartAsync(cancellationToken);
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			var transferTasks = _state.CategoryStates.Values.Select(async category =>
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					try
					{
						await category.StagingBlock.OutputAvailableAsync(stoppingToken);

						while (category.StagingBlock.TryReceive(out var item))
						{
							checkAndLogErrors(category.WorkerBlock.SendAsync(item, stoppingToken),
								category.CategoryName, _logger, _serviceScopeFactory);
						}
					}
					catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
					{
						// we are shutting down.
						break;
					}
					catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
					{
						LogError(_serviceScopeFactory, ex, DispatchLoopErrorLogMsg,
							category.CategoryName, _logger);
						await Task.Delay(100, stoppingToken); // delay to prevent tight error loops
					}
				}
			});

			static void checkAndLogErrors(Task task, string categoryName, ILogger logger,
				IServiceScopeFactory serviceScopeFactory) =>
				task.ContinueWith(t =>
				{
					if (t.IsFaulted && t.Exception is not null)
					{
						LogError(serviceScopeFactory, t.Exception, TransferErrorLogMsg,
							categoryName, logger);
					}
				}, TaskContinuationOptions.ExecuteSynchronously);

			await Task.WhenAll(transferTasks);
		}

		private static void LogError(IServiceScopeFactory serviceScopeFactory, Exception ex,
			string message, string categoryName, ILogger logger)
		{
			using var ss = serviceScopeFactory.CreateScope();
			LogError(ss.ServiceProvider, ex, message, categoryName, logger);
		}

		private static void LogError(IServiceProvider serviceProvider, Exception ex,
			string message, string categoryName, ILogger logger)
		{
			var isDefaultCategory = categoryName == OffloadWorkServiceExtensions.DefaultCategoryName;
			var customLogger = serviceProvider.GetService<IOffloadWorkExceptionLogger>();
			if (customLogger is not null)
			{
				customLogger.Log(ex, isDefaultCategory ? null : categoryName);
			}
			else if (isDefaultCategory)
			{
				logger.LogError(ex, "[OffloadWorkService] {Message}", message);
			}
			else
			{
				logger.LogError(ex,
					"[OffloadWorkService] {Message} (Category: '{CategoryName}')",
						message, categoryName);
			}
		}
	}

	/// <summary>
	/// Optional interface for handling exceptions thrown during offloaded work.
	/// 
	/// If implemented and registered via dependency injection, this logger will be invoked
	/// whenever a background work item throws an unhandled exception. This allows consumers
	/// to override or supplement default logging behavior (e.g., forward to telemetry, suppress, etc.).
	/// 
	/// If the exception occurred in the default category, <paramref name="categoryName"/> will be null.
	/// </summary>
	public interface IOffloadWorkExceptionLogger
	{
		void Log(Exception exception, string? categoryName);
	}

	public sealed class OffloadWorkServiceOptions
	{
		/// <summary>
		/// Maximum number of work items to execute in parallel within this category.
		/// Must be greater than 0. Default is 3.
		/// </summary>
		public int MaxDegreeOfParallelism { get; set; } = 3;

		/// <summary>
		/// Maximum number of active + queued work items allowed. Default is -1.
		/// -1 = unbounded
		///  0 = all work is rejected
		/// >0 = bounded capacity
		/// </summary>
		public int BoundedCapacity { get; set; } = -1;
	}

	public sealed class OffloadWorkServiceCategoryOptions
	{
		private readonly Dictionary<string, OffloadWorkServiceOptions> _categories = new();

		/// <summary>
		/// Adds a named category with the specified options.
		/// </summary>
		/// <param name="name">The unique name of the category.</param>
		/// <param name="options">The configuration options for the category (e.g., capacity, parallelism).</param>
		public void AddCategory(string name, OffloadWorkServiceOptions options) =>
			_categories[name] = options;

		/// <summary>
		/// Adds a named category with default options.
		/// </summary>
		/// <param name="name">The unique name of the category.</param>
		public void AddCategory(string name) => AddCategory(name, new());

		internal IReadOnlyDictionary<string, OffloadWorkServiceOptions> GetCategories() =>
			new ReadOnlyDictionary<string, OffloadWorkServiceOptions>(_categories);
	}

	public static class OffloadWorkServiceExtensions
	{
		internal const string DefaultCategoryName = "internal:default";

		/// <summary>
		/// Registers the offload work service with default settings and a single default category.
		/// </summary>
		/// <param name="services">The service collection.</param>
		/// <returns>The modified service collection.</returns>
		public static IServiceCollection AddOffloadWorkService(
			this IServiceCollection services) =>
			services.AddOffloadWorkService((OffloadWorkServiceOptions option) => { });

		/// <summary>
		/// Registers the offload work service with a single default category using the provided configuration.
		/// </summary>
		/// <param name="services">The service collection.</param>
		/// <param name="config">An action to configure the default category's options (e.g., parallelism, capacity).</param>
		/// <returns>The modified service collection.</returns>
		public static IServiceCollection AddOffloadWorkService(
			this IServiceCollection services, Action<OffloadWorkServiceOptions> config)
		{
			var defaultOptions = new OffloadWorkServiceOptions();
			config(defaultOptions);
			return services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions option) =>
				option.AddCategory(DefaultCategoryName, defaultOptions));
		}

		/// <summary>
		/// Registers the offload work service with support for multiple named categories.
		/// </summary>
		/// <param name="services">The service collection.</param>
		/// <param name="config">An action to configure one or more named categories and their options.</param>
		/// <returns>The modified service collection.</returns>
		/// <exception cref="InvalidOperationException">
		/// Thrown if the offload work service has already been registered in the service collection.
		/// </exception>
		public static IServiceCollection AddOffloadWorkService(
			this IServiceCollection services, Action<OffloadWorkServiceCategoryOptions> config)
		{
			if (services.Any(sd => sd.ServiceType == typeof(IOffloadWorkServiceState)))
			{
				throw new InvalidOperationException(
					$"{nameof(AddOffloadWorkService)} has already been called.");
			}

			services.Configure(config);
			services.AddTransient<IOffloadWorkService, OffloadWorkService>();
			services.AddHostedService<OffloadWorkService>();
			services.AddSingleton<IOffloadWorkServiceState, OffloadWorkServiceState>();

			return services;
		}
	}

#if NET7_0_OR_GREATER
file
#else
	internal
#endif
	sealed class CategoryState
	{
		public string CategoryName { get; }
		public ActionBlock<Func<Task>> WorkerBlock { get; }
		public BufferBlock<Func<Task>> StagingBlock { get; }
		public int BoundedCapacity { get; }
		public CancellationTokenSource CancellationTokenSource { get; }

		public object OffloadLock { get; } = new();
		public long SafeActiveCount => Interlocked.Read(ref ActiveCount);
		public long ActiveCount;

		public CategoryState(
			string categoryName,
			int boundedCapacity,
			ActionBlock<Func<Task>> worker,
			BufferBlock<Func<Task>> staging,
			CancellationTokenSource cancellationTokenSource)
		{
			CategoryName = categoryName;
			WorkerBlock = worker;
			StagingBlock = staging;
			BoundedCapacity = boundedCapacity;
			CancellationTokenSource = cancellationTokenSource;
		}
	}


#if NET7_0_OR_GREATER
	file
#else
	internal
#endif
	interface IOffloadWorkServiceState
	{
		ConcurrentDictionary<string, CategoryState> CategoryStates { get; }
	}

#if NET7_0_OR_GREATER
	file
#else
	internal
#endif
	sealed class OffloadWorkServiceState : IOffloadWorkServiceState, IDisposable
	{
		public ConcurrentDictionary<string, CategoryState> CategoryStates { get; } = new();

		public void Dispose()
		{
			foreach (var cts in CategoryStates.Select(x => x.Value.CancellationTokenSource))
			{
				cts.Cancel();
			}
		}
	}

	/// <summary>
	/// Internal testing extensions to support unit testing of file-scoped services, such as
	/// OffloadWorkService.
	/// </summary>
	[ExcludeFromCodeCoverage, EditorBrowsable(EditorBrowsableState.Never)]
	internal static class InternalTestingExtensions
	{
		public static Task StartHostedOffloadWorkServiceAsync(
			this IServiceProvider serviceProvider, CancellationToken cancellationToken)
		{
			var s = serviceProvider.GetServices<IHostedService>()
				.OfType<OffloadWorkService>().Single();
			return s.StartAsync(cancellationToken);
		}

		public static Task StopHostedOffloadWorkServiceAsync(
			this IServiceProvider serviceProvider, CancellationToken cancellationToken)
		{
			var s = serviceProvider.GetServices<IHostedService>()
				.OfType<OffloadWorkService>().Single();
			return s.StopAsync(cancellationToken);
		}

		public static TResult AddCustomLogger<TResult>(
			this IServiceCollection services, Type loggerType)
		{
			if (!loggerType.IsGenericTypeDefinition ||
				!loggerType.GetInterfaces().Any(i => i.IsGenericType &&
					i.GetGenericTypeDefinition() == typeof(ILogger<>)))
			{
				throw new ArgumentException(
					"Provided type must implement ILogger<>", nameof(loggerType));
			}

			var gLogger = loggerType.MakeGenericType(typeof(OffloadWorkService));
			var loggerInst = (ILogger<OffloadWorkService>)(Activator.CreateInstance(gLogger) ??
				throw new InvalidOperationException(
					$"Unable to create logger {loggerType.Name}."));

			services.AddSingleton(loggerInst);

			if (loggerInst is not TResult result)
			{
				throw new InvalidCastException(
					$"{loggerInst.GetType()} does not implement {typeof(TResult)}");
			}

			return result;
		}
	}
}
