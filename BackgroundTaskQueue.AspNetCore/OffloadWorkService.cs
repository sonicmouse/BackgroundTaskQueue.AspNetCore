using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Tasks.Dataflow;

namespace BackgroundTaskQueue.AspNetCore
{
	#region IOffloadWorkService

	public interface IOffloadWorkService
	{
		bool Offload<T>(Func<IServiceProvider, T, CancellationToken, Task> work, T param);

		int GetQueueLength();
	}

#if NET8_0_OR_GREATER
	file
#else
	internal
#endif
	sealed class OffloadWorkService : BackgroundService, IOffloadWorkService
	{
		private readonly ILogger<OffloadWorkService> _logger;
		private readonly IServiceScopeFactory _serviceScopeFactory;
		private readonly IOffloadWorkServiceDependencies _dep;

		public OffloadWorkService(ILogger<OffloadWorkService> logger,
			IServiceScopeFactory serviceScopeFactory,
			IOffloadWorkServiceDependencies serviceDependencies)
		{
			_logger = logger;
			_serviceScopeFactory = serviceScopeFactory;
			_dep = serviceDependencies;
		}

		public bool Offload<T>(Func<IServiceProvider, T, CancellationToken, Task> work, T param)
		{
			var serviceScope = _serviceScopeFactory.CreateAsyncScope();
			return _dep.TaskQueue.Post(() =>
				RunScopedWorkAsync(serviceScope, work, param, _dep.CancellationTokenSource.Token));
		}

		public int GetQueueLength() => _dep.TaskQueue.Count;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			using var linkedCts = CancellationTokenSource
				.CreateLinkedTokenSource(stoppingToken, _dep.CancellationTokenSource.Token);
			var sharedToken = linkedCts.Token;

			var taskProcessor = new ActionBlock<Func<Task>>(async work =>
			{
				try
				{
					await work();
				}
				catch (Exception ex)
				{
					_logger.LogError(ex,
						"Error processing offloaded task in {Object}", nameof(ExecuteAsync));
				}
			}, new ExecutionDataflowBlockOptions
			{
				MaxDegreeOfParallelism = _dep.MaxDegreeOfParallelism,
				CancellationToken = sharedToken
			});

			while (!sharedToken.IsCancellationRequested)
			{
				try
				{
					var work = await _dep.TaskQueue.ReceiveAsync(sharedToken);
					await taskProcessor.SendAsync(work, sharedToken);
				}
				catch (OperationCanceledException)
				{
					// Service shutting down
				}
			}
		}

		private async Task RunScopedWorkAsync<T>(
			AsyncServiceScope serviceScope,
			Func<IServiceProvider, T, CancellationToken, Task> work,
			T param,
			CancellationToken cancellationToken)
		{
			await using (serviceScope)
			{
				try
				{
					await work(serviceScope.ServiceProvider, param, cancellationToken);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex,
						"Error processing offloaded task in {Object}", nameof(RunScopedWorkAsync));
				}
			}
		}
	}

#endregion

	#region Extensions

	public sealed class OffloadWorkServiceOptions
	{
		public int MaxDegreeOfParallelism { get; set; }
		public int BoundedCapacity { get; set; }
	}

	public static class OffloadWorkServiceExtensions
	{
		public static IServiceCollection AddOffloadWorkService(
			this IServiceCollection services)
		{
			return services.AddOffloadWorkService((options) =>
			{
				options.MaxDegreeOfParallelism = 3;
				options.BoundedCapacity = -1;
			});
		}

		public static IServiceCollection AddOffloadWorkService(
			this IServiceCollection services, Action<OffloadWorkServiceOptions> options)
		{
			services.Configure(options);
			services.AddTransient<IOffloadWorkService, OffloadWorkService>();
			services.AddSingleton<IOffloadWorkServiceDependencies, OffloadWorkServiceDependencies>();

			services.AddHostedService<OffloadWorkService>();

			return services;
		}
	}

	#endregion

	#region IOffloadWorkServiceDependencies

#if NET8_0_OR_GREATER
	file
#else
	internal
# endif
	interface IOffloadWorkServiceDependencies
	{
		BufferBlock<Func<Task>> TaskQueue { get; }
		CancellationTokenSource CancellationTokenSource { get; }
		int MaxDegreeOfParallelism { get; }
	}

#if NET8_0_OR_GREATER
	file
#else
	internal
#endif
	sealed class OffloadWorkServiceDependencies : IOffloadWorkServiceDependencies, IDisposable
	{
		public OffloadWorkServiceDependencies(IOptions<OffloadWorkServiceOptions> options)
		{
			MaxDegreeOfParallelism = options.Value.MaxDegreeOfParallelism;
			TaskQueue = new(new()
			{
				BoundedCapacity = options.Value.BoundedCapacity
			});
		}

		public BufferBlock<Func<Task>> TaskQueue { get; }

		public CancellationTokenSource CancellationTokenSource { get; } = new();

		public int MaxDegreeOfParallelism { get; }

		public void Dispose() => CancellationTokenSource.Dispose();
	}

#endregion
}
