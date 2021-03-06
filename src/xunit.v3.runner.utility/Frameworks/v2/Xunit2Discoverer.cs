﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Runner.Common;
using Xunit.Sdk;

namespace Xunit
{
	/// <summary>
	/// This class be used to do discovery of xUnit.net v2 tests, via any implementation
	/// of <see cref="IAssemblyInfo"/>, including AST-based runners like CodeRush and
	/// Resharper. Runner authors who are not using AST-based discovery are strongly
	/// encouraged to use <see cref="XunitFrontController"/> instead.
	/// </summary>
	public class Xunit2Discoverer : ITestFrameworkDiscoverer, ITestCaseDescriptorProvider
	{
#if NETFRAMEWORK
		static readonly string[] SupportedPlatforms = { "dotnet", "desktop" };
		static readonly string[] SupportedPlatforms_ForcedAppDomains = { "desktop" };
		readonly AssemblyHelper? assemblyHelper;
#else
		static readonly string[] SupportedPlatforms = { "dotnet" };
#endif

		ITestCaseDescriptorProvider? defaultTestCaseDescriptorProvider;

		/// <summary>
		/// Initializes a new instance of the <see cref="Xunit2Discoverer"/> class.
		/// </summary>
		/// <param name="diagnosticMessageSink">The message sink which receives <see cref="IDiagnosticMessage"/> messages.</param>
		/// <param name="appDomainSupport">Determines whether tests should be run in a separate app domain.</param>
		/// <param name="sourceInformationProvider">The source code information provider.</param>
		/// <param name="assemblyInfo">The assembly to use for discovery</param>
		/// <param name="xunitExecutionAssemblyPath">The path on disk of xunit.execution.*.dll; if <c>null</c>, then
		/// the location of xunit.execution.*.dll is implied based on the location of the test assembly</param>
		/// <param name="shadowCopyFolder">The path on disk to use for shadow copying; if <c>null</c>, a folder
		/// will be automatically (randomly) generated</param>
		/// <param name="verifyAssembliesOnDisk">Determines whether or not to check for the existence of assembly files.</param>
		public Xunit2Discoverer(
			IMessageSink diagnosticMessageSink,
			AppDomainSupport appDomainSupport,
			ISourceInformationProvider sourceInformationProvider,
			IAssemblyInfo assemblyInfo,
			string? xunitExecutionAssemblyPath = null,
			string? shadowCopyFolder = null,
			bool verifyAssembliesOnDisk = true)
				: this(
					diagnosticMessageSink,
					appDomainSupport,
					sourceInformationProvider,
					assemblyInfo,
					null,
					xunitExecutionAssemblyPath ?? GetXunitExecutionAssemblyPath(appDomainSupport, assemblyInfo),
					null,
					true,
					shadowCopyFolder,
					verifyAssembliesOnDisk
				)
		{ }

		// Used by Xunit2 when initializing for both discovery and execution.
		internal Xunit2Discoverer(
			IMessageSink diagnosticMessageSink,
			AppDomainSupport appDomainSupport,
			ISourceInformationProvider sourceInformationProvider,
			string assemblyFileName,
			string? configFileName,
			bool shadowCopy,
			string? shadowCopyFolder = null,
			bool verifyAssembliesOnDisk = true)
				: this(
					diagnosticMessageSink,
					appDomainSupport,
					sourceInformationProvider,
					null,
					assemblyFileName,
					GetXunitExecutionAssemblyPath(appDomainSupport, assemblyFileName, verifyAssembliesOnDisk),
					configFileName,
					shadowCopy,
					shadowCopyFolder,
					verifyAssembliesOnDisk
				)
		{ }

		Xunit2Discoverer(
			IMessageSink diagnosticMessageSink,
			AppDomainSupport appDomainSupport,
			ISourceInformationProvider sourceInformationProvider,
			IAssemblyInfo? assemblyInfo,
			string? assemblyFileName,
			string xunitExecutionAssemblyPath,
			string? configFileName,
			bool shadowCopy,
			string? shadowCopyFolder,
			bool verifyAssembliesOnDisk)
		{
			Guard.ArgumentNotNull(nameof(diagnosticMessageSink), diagnosticMessageSink);
			Guard.ArgumentNotNull(nameof(assemblyInfo), (object?)assemblyInfo ?? assemblyFileName);

#if NETFRAMEWORK
			// Only safe to assume the execution reference is copied in a desktop project
			if (verifyAssembliesOnDisk)
				Guard.FileExists("xunitExecutionAssemblyPath", xunitExecutionAssemblyPath);

			CanUseAppDomains = !IsDotNet(xunitExecutionAssemblyPath);
#else
			CanUseAppDomains = false;
#endif

			DiagnosticMessageSink = diagnosticMessageSink;

			var appDomainAssembly = assemblyFileName ?? xunitExecutionAssemblyPath;
			AppDomain = AppDomainManagerFactory.Create(appDomainSupport != AppDomainSupport.Denied && CanUseAppDomains, appDomainAssembly, configFileName, shadowCopy, shadowCopyFolder, DiagnosticMessageSink);

#if NETFRAMEWORK
			var runnerUtilityAssemblyLocation = Path.GetDirectoryName(typeof(AssemblyHelper).Assembly.GetLocalCodeBase());
			assemblyHelper = AppDomain.CreateObjectFrom<AssemblyHelper>(typeof(AssemblyHelper).Assembly.Location, typeof(AssemblyHelper).FullName!, runnerUtilityAssemblyLocation);
#endif

			TestFrameworkAssemblyName = GetTestFrameworkAssemblyName(xunitExecutionAssemblyPath);

			// If we didn't get an assemblyInfo object, we can leverage the reflection-based IAssemblyInfo wrapper
			if (assemblyInfo == null)
				assemblyInfo = AppDomain.CreateObject<IAssemblyInfo>(TestFrameworkAssemblyName, "Xunit.Sdk.ReflectionAssemblyInfo", assemblyFileName);

			Framework = Guard.NotNull("Could not create Xunit.Sdk.TestFrameworkProxy for v2 unit test", AppDomain.CreateObject<ITestFramework>(TestFrameworkAssemblyName, "Xunit.Sdk.TestFrameworkProxy", assemblyInfo, sourceInformationProvider, DiagnosticMessageSink));
			RemoteDiscoverer = Guard.NotNull("Could not get discoverer from test framework for v2 unit test", Framework.GetDiscoverer(assemblyInfo));
		}

		internal IAppDomainManager AppDomain { get; }

		/// <summary>
		/// Gets a value indicating whether the tests can use app domains (must be linked against desktop execution library).
		/// </summary>
		public bool CanUseAppDomains { get; }

		/// <summary>
		/// Gets the message sink used to report diagnostic messages.
		/// </summary>
		public IMessageSink DiagnosticMessageSink { get; }

		/// <summary>
		/// Returns the test framework from the remote app domain.
		/// </summary>
		public ITestFramework Framework { get; }

		internal ITestFrameworkDiscoverer RemoteDiscoverer { get; }

		/// <inheritdoc/>
		public string TargetFramework => RemoteDiscoverer.TargetFramework;

		internal AssemblyName TestFrameworkAssemblyName { get; }

		/// <inheritdoc/>
		public string TestFrameworkDisplayName => RemoteDiscoverer.TestFrameworkDisplayName;

		/// <summary>
		/// Creates a high performance cross AppDomain message sink that utilizes <see cref="IMessageSinkWithTypes"/>
		/// which can be passed to <see cref="ITestFrameworkDiscoverer"/> and <see cref="ITestFrameworkExecutor"/>.
		/// </summary>
		/// <param name="sink">The local message sink to receive the messages.</param>
		protected IMessageSink CreateOptimizedRemoteMessageSink(IMessageSink sink)
		{
			Guard.ArgumentNotNull(nameof(sink), sink);

			try
			{
				var sinkWithTypes = MessageSinkWithTypesAdapter.Wrap(sink);
				var asssemblyName = typeof(OptimizedRemoteMessageSink).Assembly.GetName();
				var optimizedSink = AppDomain.CreateObject<IMessageSink>(asssemblyName, typeof(OptimizedRemoteMessageSink).FullName!, sinkWithTypes);
				if (optimizedSink != null)
					return optimizedSink;
			}
			catch { }    // This really shouldn't happen, but falling back makes sense in catastrophic cases

			return sink;
		}

		/// <inheritdoc/>
		public virtual void Dispose()
		{
			RemoteDiscoverer?.Dispose();
			Framework?.Dispose();
#if NETFRAMEWORK
			assemblyHelper?.Dispose();
#endif
			AppDomain?.Dispose();
		}

		/// <summary>
		/// Starts the process of finding all xUnit.net v2 tests in an assembly.
		/// </summary>
		/// <param name="includeSourceInformation">Whether to include source file information, if possible.</param>
		/// <param name="messageSink">The message sink to report results back to.</param>
		/// <param name="discoveryOptions">The options used by the test framework during discovery.</param>
		public void Find(bool includeSourceInformation, IMessageSink messageSink, ITestFrameworkDiscoveryOptions discoveryOptions)
		{
			Guard.ArgumentNotNull(nameof(messageSink), messageSink);
			Guard.ArgumentNotNull(nameof(discoveryOptions), discoveryOptions);

			RemoteDiscoverer.Find(includeSourceInformation, CreateOptimizedRemoteMessageSink(messageSink), discoveryOptions);
		}

		/// <summary>
		/// Starts the process of finding all xUnit.net v2 tests in a class.
		/// </summary>
		/// <param name="typeName">The fully qualified type name to find tests in.</param>
		/// <param name="includeSourceInformation">Whether to include source file information, if possible.</param>
		/// <param name="messageSink">The message sink to report results back to.</param>
		/// <param name="discoveryOptions">The options used by the test framework during discovery.</param>
		public void Find(string typeName, bool includeSourceInformation, IMessageSink messageSink, ITestFrameworkDiscoveryOptions discoveryOptions)
		{
			Guard.ArgumentNotNull(nameof(messageSink), messageSink);
			Guard.ArgumentNotNull(nameof(discoveryOptions), discoveryOptions);

			RemoteDiscoverer.Find(typeName, includeSourceInformation, CreateOptimizedRemoteMessageSink(messageSink), discoveryOptions);
		}

		/// <inheritdoc/>
		public List<TestCaseDescriptor> GetTestCaseDescriptors(List<ITestCase> testCases, bool includeSerialization)
		{
			Guard.ArgumentNotNull(nameof(testCases), testCases);

			var callbackContainer = new DescriptorCallback();
			Action<List<string>> callback = callbackContainer.Callback;

			if (defaultTestCaseDescriptorProvider == null)
			{
				if (AppDomain.HasAppDomain)
				{
					try
					{
						AppDomain.CreateObject<object>(TestFrameworkAssemblyName, "Xunit.Sdk.TestCaseDescriptorFactory", includeSerialization ? RemoteDiscoverer : null, testCases, callback);
						if (callbackContainer.Results != null)
							return callbackContainer.Results.Select(x => new TestCaseDescriptor(x)).ToList();
					}
					catch (TypeLoadException) { }    // Only be willing to eat "Xunit.Sdk.TestCaseDescriptorFactory" doesn't exist
				}

				defaultTestCaseDescriptorProvider = new DefaultTestCaseDescriptorProvider(RemoteDiscoverer);
			}

			return defaultTestCaseDescriptorProvider.GetTestCaseDescriptors(testCases, includeSerialization);
		}

		static string GetExecutionAssemblyFileName(AppDomainSupport appDomainSupport, string basePath)
		{
			var supportedPlatformSuffixes = GetSupportedPlatformSuffixes(appDomainSupport);

			foreach (var suffix in supportedPlatformSuffixes)
			{
#if NETFRAMEWORK
				var fileName = Path.Combine(basePath, $"xunit.execution.{suffix}.dll");
				if (File.Exists(fileName))
					return fileName;
#else
				try
				{
					var assemblyName = $"xunit.execution.{suffix}";
					Assembly.Load(new AssemblyName { Name = assemblyName });
					return assemblyName + ".dll";
				}
				catch { }
#endif
			}

			throw new InvalidOperationException("Could not find/load any of the following assemblies: " + string.Join(", ", supportedPlatformSuffixes.Select(suffix => $"xunit.execution.{suffix}.dll").ToArray()));
		}

		static string[] GetSupportedPlatformSuffixes(AppDomainSupport appDomainSupport)
		{
#if NETFRAMEWORK
			return appDomainSupport == AppDomainSupport.Required ? SupportedPlatforms_ForcedAppDomains : SupportedPlatforms;
#else
			return SupportedPlatforms;
#endif
		}

		static AssemblyName GetTestFrameworkAssemblyName(string xunitExecutionAssemblyPath)
		{
#if NETFRAMEWORK
			return AssemblyName.GetAssemblyName(xunitExecutionAssemblyPath);
#else
			// Make sure we only use the short form
			return Assembly.Load(new AssemblyName { Name = Path.GetFileNameWithoutExtension(xunitExecutionAssemblyPath), Version = new Version(0, 0, 0, 0) }).GetName();
#endif
		}

		static string GetXunitExecutionAssemblyPath(AppDomainSupport appDomainSupport, string assemblyFileName, bool verifyTestAssemblyExists)
		{
			Guard.ArgumentNotNullOrEmpty("assemblyFileName", assemblyFileName);
			if (verifyTestAssemblyExists)
				Guard.FileExists("assemblyFileName", assemblyFileName);

			return GetExecutionAssemblyFileName(appDomainSupport, Path.GetDirectoryName(assemblyFileName)!);
		}

		static string GetXunitExecutionAssemblyPath(AppDomainSupport appDomainSupport, IAssemblyInfo assemblyInfo)
		{
			Guard.ArgumentNotNull("assemblyInfo", assemblyInfo);
			Guard.ArgumentNotNullOrEmpty("assemblyInfo.AssemblyPath", assemblyInfo.AssemblyPath);

			return GetExecutionAssemblyFileName(appDomainSupport, Path.GetDirectoryName(assemblyInfo.AssemblyPath)!);
		}

#if NETFRAMEWORK
		static bool IsDotNet(string executionAssemblyFileName) =>
			executionAssemblyFileName.EndsWith(".dotnet.dll", StringComparison.Ordinal);
#endif

		/// <inheritdoc/>
		public string Serialize(ITestCase testCase) =>
			RemoteDiscoverer.Serialize(testCase);

		class DescriptorCallback : LongLivedMarshalByRefObject
		{
			public List<string>? Results;

			public void Callback(List<string> results) => Results = results;
		}
	}
}
