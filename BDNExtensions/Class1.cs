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

		private long PeakMemory = 0;
		private float TotalProcessorTime = 0;
		private float beforeCpu = 0;
		TimeSpan? lastCpu = null;
		int id = 0;
		Task? t = null;
		private PerformanceCounter cpuCounter;
		private int coreCount;
		private Process currentProcess;

		public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
		{
			CancellationTokenSource cts = new CancellationTokenSource();
			
			if (signal == HostSignal.BeforeActualRun)
			{
				id = parameters.Process.Id;
				var p = Process.GetProcessById(id);
				
				this.currentProcess = Process.GetCurrentProcess();
				cpuCounter = new PerformanceCounter("Process", "% Processor Time", currentProcess.ProcessName);
				this.coreCount = Environment.ProcessorCount;
				
				beforeCpu = this.cpuCounter.NextValue();
				
				t = Task.Factory.StartNew(async () =>
					{
						while (!cts.IsCancellationRequested)
						{
							try
							{
								var p = Process.GetProcessById(id);
								var peak = p.WorkingSet64;
								var afterCpu  = this.cpuCounter.NextValue();
								TotalProcessorTime = afterCpu - beforeCpu;
								PeakMemory = Math.Max(PeakMemory, peak);
								Console.WriteLine($"{TotalProcessorTime}s, {PeakMemory}b, {p.Id}");
							}
							catch (Exception e)
							{
								
							}

							await Task.Delay(500, cts.Token);
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
			yield return new Metric(CpuUserMetricDescriptor.Instance, TotalProcessorTime * 1000_000);
			yield return new Metric(PeakMemoryMetricDescriptor.Instance, PeakMemory);
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