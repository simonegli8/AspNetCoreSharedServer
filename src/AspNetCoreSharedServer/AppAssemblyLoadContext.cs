using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Loader;
using System.Reflection;

namespace AspNetCoreSharedServer;

public class AppAssemblyLoadContext: AssemblyLoadContext
{
	public string AssemblyPath { get; private set; }
	public AssemblyDependencyResolver Resolver { get; private set; }
	public AppAssemblyLoadContext(string assemblyPath): base(true)
	{
		AssemblyPath = assemblyPath;
		Resolver = new AssemblyDependencyResolver(assemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		var path = Resolver.ResolveAssemblyToPath(assemblyName);
		if (path != null && File.Exists(path)) return LoadFromAssemblyPath(path);
		return null;
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
	{
		var path = Resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		if (path != null && File.Exists(path)) return LoadUnmanagedDllFromPath(path);
		return IntPtr.Zero;
	}
}
