using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Benchmarks
{
	public class CpuDiagnoserAttribute : Attribute, IConfigSource
	{
		public IConfig Config { get; }

		public CpuDiagnoserAttribute()
		{
			Config = ManualConfig.CreateEmpty().AddDiagnoser(new CpuDiagnoser());
		}
	}

	public class CpuDiagnoser : IDiagnoser
	{
		private Process proc;
		
		public CpuDiagnoser()
		{
			proc = Process.GetCurrentProcess();
		}

		public IEnumerable<string> Ids => new[] { "CPU" };

		public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();

		public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

		public void DisplayResults(ILogger logger)
		{
		}

		public RunMode GetRunMode(BenchmarkCase benchmarkCase)
		{
			return RunMode.NoOverhead;
		}

		long userStart, userEnd;
		long privStart, privEnd;

		private long PeakMemory = 0;
		private TimeSpan TotalProcessorTime = TimeSpan.Zero;
		private TimeSpan beforeCpu = TimeSpan.Zero;
		TimeSpan? lastCpu = null;
		int id = 0;
		Task? t = null;
		
		public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
		{
			CancellationTokenSource cts = new CancellationTokenSource();
			
			if (signal == HostSignal.BeforeActualRun)
			{
				id = parameters.Process.Id;
				var p = Process.GetProcessById(id);
				beforeCpu = p.TotalProcessorTime;
				
				t = Task.Factory.StartNew(async () =>
					{
						while (!cts.IsCancellationRequested)
						{
							try
							{
								var p = Process.GetProcessById(id);
								var peak = p.WorkingSet64;
								var afterCpu  = p.TotalProcessorTime;
								TotalProcessorTime = afterCpu.Subtract(beforeCpu);
								PeakMemory = Math.Max(PeakMemory, peak);
								Console.WriteLine($"{TotalProcessorTime}s, {PeakMemory}b, {p.Id}");
							}
							catch (Exception e)
							{
								
							}

							await Task.Delay(50, cts.Token);
						}
					}, cts.Token
				);
			} else if (signal == HostSignal.AfterActualRun)
			{
				cts.Cancel();
				t.Wait();
			}
		}

		public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
		{
			yield return new Metric(CpuUserMetricDescriptor.Instance, TotalProcessorTime.TotalMilliseconds * 1000_000);
			yield return new Metric(PeakMemoryMetricDescriptor.Instance, PeakMemory);
			// yield return new Metric(CpuUserMetricDescriptor.Instance, 1000000000);
			// yield return new Metric(PeakMemoryMetricDescriptor.Instance, 1000000000);
		}

		public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
		{
			yield break;
		}

		class CpuUserMetricDescriptor : IMetricDescriptor
		{
			internal static readonly IMetricDescriptor Instance = new CpuUserMetricDescriptor();

			public string Id => "TotalProcessorTime";
			public string DisplayName => Id;
			public string Legend => Id;
			public string NumberFormat => "0.##";
			public UnitType UnitType => UnitType.Time;
			public string Unit => "us";
			public bool TheGreaterTheBetter => false;
			public int PriorityInCategory => 1;
		}

		class PeakMemoryMetricDescriptor : IMetricDescriptor
		{
			internal static readonly IMetricDescriptor Instance = new PeakMemoryMetricDescriptor();

			public string Id => "PeakWorkingSet64";
			public string DisplayName => Id;
			public string Legend => Id;
			public string NumberFormat => "000.###";
			public UnitType UnitType => UnitType.Size;
			public string Unit => "MB";
			public bool TheGreaterTheBetter => false;
			public int PriorityInCategory => 1;
		}
	}
}