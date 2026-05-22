using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer.Services;


public abstract class Shell
{
	const bool DoNotWaitForProcessExit = false;

	static int N = 0;

	public int ShellId = N++;
	public Shell() : base()
	{
		output = new StringBuilder();
		error = new StringBuilder();
		outputAndError = new StringBuilder();
		Log += OnLog;
		LogCommand += OnLogCommand;
		LogCommandEnd += OnLogCommandEnd;
		LogError += OnLogError;
		LogOutput += OnLogOutput;
            CompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CompletionSource.TrySetResult(this);
        }

        protected SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
	protected SemaphoreSlim OutputLock = new SemaphoreSlim(1, 1);
	protected SemaphoreSlim ErrorLock = new SemaphoreSlim(1, 1);
	protected SemaphoreSlim OutputAndErrorLock = new SemaphoreSlim(1, 1);

        public TaskCompletionSource<Shell> CompletionSource;

        //methods to support await on Shell type
        //public TaskAwaiter<Shell> GetAwaiter() => CompletionSource.Task.GetAwaiter();
        public TaskAwaiter<Shell> GetAwaiter()
        {
            try
            {
                return CompletionSource.Task.GetAwaiter();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetAwaiter failed: " + ex);
                throw;
            }
        }

        bool errorEOF = true, outputEOF = true, hasProcessExited = true;
	int exitCode = 0;

	public Shell Parent { get; set; } = null;
	public virtual char PathSeparator => Path.PathSeparator;
	public bool CreateNoWindow = true;
	public bool UseShellExecute = false;
	public string User { get; set; }
	public string Password { get; set; }
        public virtual string WorkingDirectory { get; set; } = null;
	public Encoding Encoding = null;
	public Dictionary<string, string> Environment = new Dictionary<string, string>();

	public ProcessWindowStyle WindowStyle = ProcessWindowStyle.Minimized;
	public bool RedirectOutput { get; set; } = true;
	public abstract string ShellExe { get; }

	public Process Process = null;
	public bool NotFound { get; set; }
	public static IEnumerable<string> Paths
	{
		get
		{
			string proc, machine = "", user = "";
			string[] sources;
			proc = System.Environment.GetEnvironmentVariable("PATH");
			if (IsWindows)
			{
				machine = System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
				user = System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
				var process = System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
				sources = new string[] {
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.SystemX86),
					process, machine, user };
			}
			else sources = new string[] { proc };

			return sources
				.SelectMany(paths => paths.Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
				.Select(path => path.Trim())
				.Distinct();
		}
	}

	public virtual string Find(string cmd)
	{
		string file = null;
		cmd = cmd.Trim('"');
		if (cmd.IndexOf(Path.DirectorySeparatorChar) >= 0)
		{
			if (File.Exists(cmd)) file = cmd;
		}
		else
		{
			file = Paths
				  .SelectMany(p =>
				  {
					  var p1 = Path.Combine(p, cmd);
					  return new string[] { p1, Path.ChangeExtension(p1, "exe") };
				  })
				  .FirstOrDefault(p => File.Exists(p));
		}
		NotFound = file == null;
		return file;
	}

	protected virtual string ToTempFile(string script)
	{
		var file = Path.GetTempFileName();
		File.WriteAllText(file, script);
		return file;
	}

	int isInCheckComplete = 0;
        bool CheckCompleted()
        {
		if (Interlocked.Exchange(ref isInCheckComplete, 1) == 1) return false;

		try {
			var proc = Process;
			bool exited = DoNotWaitForProcessExit || hasProcessExited || proc == null;
			if (!exited) exited = proc.HasExited;
			if (exited && errorEOF && outputEOF)
			{
				int n = 0;
				do
				{
					try
					{
						exitCode = proc?.ExitCode ?? -500;
					} catch (System.InvalidOperationException) {
						n++;
						Thread.Sleep(10);
					}
				} while(n > 0 && n < 100); // wait 10 seconds
				//Debug.WriteLine("CheckComplete succcess");
                    CompletionSource.TrySetResult(this);
				return true;
			}
            }
            finally {
                Interlocked.Exchange(ref isInCheckComplete, 0);
            }
		return false;
        }

	public bool IsCompleted => CheckCompleted();

        private SecureString ToSecureString(string password)
        {
            var secure = new SecureString();
            foreach (char c in password)
                secure.AppendChar(c);

            secure.MakeReadOnly();
            return secure;
        }

        //public virtual StreamWriter StandardInput => Process?.StandardInput;
        public virtual Shell ExecAsync(string cmd, Encoding encoding = null, Dictionary<string, string> environment = null)
	{
		bool impersonate = !IsWindows && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password);
		if (impersonate)
		{
			var sudocmd = $"sudo -u {User} ";
			cmd = sudocmd + cmd;
            }
            var parent = this;
		while (parent != null)
		{
			parent.LogCommand?.Invoke(cmd);
			parent = parent.Parent;
		}

		// separate command from arguments
		string arguments;
		if (cmd.Length > 0 && cmd[0] == '"') // command is a " delimited string
		{
			var pos = cmd.IndexOf('"', 1);
			if (pos >= 1)
			{
				if (pos < cmd.Length - 1)
				{
					arguments = cmd.Substring(pos + 1).Trim();
					cmd = cmd.Substring(1, pos - 1);
				}
				else
				{
					cmd = cmd.Substring(1, pos - 1);
					arguments = "";
				}
			}
			else
			{
				cmd = cmd.Substring(1);
				arguments = "";
			}
		}
		else // command is the first token of space separated tokens
		{
			var pos = cmd.IndexOf(' ');
			if (pos >= 0 && pos < cmd.Length - 1)
			{
				arguments = cmd.Substring(pos + 1);
				cmd = cmd.Substring(0, pos);
			}
			else arguments = "";
		}

		var cmdWithPath = Find(cmd);
		if (cmdWithPath != null)
		{
                var child = Clone;
                CompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                child.CompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
			hasProcessExited = errorEOF = outputEOF = child.hasProcessExited = child.errorEOF = child.outputEOF = false;

			// Reset output
                output = new StringBuilder();
                error = new StringBuilder();
                outputAndError = new StringBuilder();

                var process = new Process();
			Process = child.Process = process;
			process.StartInfo.FileName = cmdWithPath;
			process.StartInfo.Arguments = arguments;
			process.StartInfo.UseShellExecute = UseShellExecute;
			process.StartInfo.CreateNoWindow = CreateNoWindow;
			process.StartInfo.WindowStyle = WindowStyle;
			process.StartInfo.WorkingDirectory = WorkingDirectory ??
			process.StartInfo.WorkingDirectory;
			process.StartInfo.RedirectStandardOutput = RedirectOutput;
			process.StartInfo.RedirectStandardError = RedirectOutput;
			process.StartInfo.RedirectStandardInput = impersonate || RedirectOutput;
			if (!string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password))
			{
				if (IsWindows)
                    {
                        process.StartInfo.UserName = User;
                        process.StartInfo.Password = ToSecureString(Password);
                        process.StartInfo.LoadUserProfile = true;
                    }
			}
			if (RedirectOutput)
			{
				process.StartInfo.StandardOutputEncoding = encoding ?? Encoding ?? Encoding.Default;
				process.StartInfo.StandardErrorEncoding = encoding ?? Encoding ?? Encoding.Default;
			}
			var env = environment ?? Environment;
			if (env != null)
			{
				foreach (var variable in env)
				{
					if (!process.StartInfo.EnvironmentVariables.ContainsKey(variable.Key))
						process.StartInfo.EnvironmentVariables.Add(variable.Key, variable.Value);
					else
						process.StartInfo.EnvironmentVariables[variable.Key] = variable.Value;
				}
			}
			process.Exited += (obj, args) =>
			{
				hasProcessExited = child.hasProcessExited = true;
				child.CheckCompleted();
				CheckCompleted();
			};
			process.EnableRaisingEvents = true;
			process.ErrorDataReceived += (p, data) =>
			{
				if (data.Data == null)
				{
					errorEOF = child.errorEOF = true;
					child.CheckCompleted();
					CheckCompleted();
				}
				else
				{
					var line = $"{data.Data}{System.Environment.NewLine}";
					var shell = child;
					while (shell != null)
					{
						shell.Log?.Invoke(line);
						shell.LogError?.Invoke(line);
						shell = shell.Parent;
					}
				}
			};
			process.OutputDataReceived += (p, data) =>
			{
				if (data.Data == null)
				{
					outputEOF = child.outputEOF = true;
					child.CheckCompleted();
					CheckCompleted();
				}
				else
				{
					var line = $"{data.Data}{System.Environment.NewLine}";
					var shell = child;
					while (shell != null)
					{
						shell.Log?.Invoke(line);
						shell.LogOutput?.Invoke(line);
						shell = shell.Parent;
					}
				}
			};
			if (!RedirectOutput)
                {
                    errorEOF = outputEOF = child.errorEOF = child.outputEOF = true;
                }
                process.Start();
			if (RedirectOutput)
			{
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.StandardInput.AutoFlush = true;
			}
			else if (impersonate)
			{
				process.StandardInput.AutoFlush = true;
			}
			if (impersonate) Input.WriteLine(Password);
			return child;
		}
		else
		{
			LogError?.Invoke($"Error {cmd} not found.{System.Environment.NewLine}");
			var child = Clone;
			child.Process = null;
			child.NotFound = true;
			var ex = new FileNotFoundException();
                Debug.WriteLine($"Error {cmd} not found.");
                CompletionSource.TrySetException(ex);
			child.CompletionSource.TrySetException(ex);
			return child;
		}
	}
	public virtual Shell Exec(string command, Encoding encoding = null, Dictionary<string, string> environment = null)
	{
		return ExecAsync(command, encoding, environment).Wait();
        }
	public virtual Shell Clone
	{
		get
		{
			Shell clone = Activator.CreateInstance(GetType()) as Shell;
			clone.Parent = this;
			clone.CreateNoWindow = this.CreateNoWindow;
			clone.WindowStyle = this.WindowStyle;
			clone.UseShellExecute = this.UseShellExecute;
			clone.RedirectOutput = this.RedirectOutput;
			clone.WorkingDirectory = this.WorkingDirectory;
			clone.Encoding = this.Encoding;
			clone.User = this.User;
			clone.Password = this.Password;
			clone.Environment = new Dictionary<string, string>();
			foreach (var item in this.Environment) clone.Environment.Add(item.Key, item.Value);

			return clone;
		}
	}

	public virtual Shell SilentClone
	{
		get
		{
			var clone = Clone;
			clone.Log = clone.LogCommand = clone.LogOutput = clone.LogError = null;
			clone.Parent = null;
			return clone;
		}
	}

	public virtual Shell ExecScriptAsync(string script, string args = null, Encoding encoding = null, Dictionary<string, string> environment = null)
	{
		script = script.Trim();
		// adjust new lines to OS type
		script = Regex.Replace(script, @"\r?\n", System.Environment.NewLine);
		var file = ToTempFile(script.Trim());
		var cmd = new StringBuilder();
		cmd.Append(ShellExe);
		cmd.Append(" \"");
		cmd.Append(file);
		cmd.Append("\"");
		if (args != null)
		{
			cmd.Append(" ");
			cmd.Append(args);
		}
		var shell = ExecAsync(cmd.ToString(), encoding, environment);
            shell.Task.ContinueWith(_ =>
            {
                try { File.Delete(file); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            });
		return shell;
	}

	public virtual Shell ExecScript(string script, string args = null, Encoding encoding = null, Dictionary<string, string> environment = null)
        {
		return ExecScriptAsync(script, args, encoding, environment).Wait();
        }

        /* public virtual async Task<Shell> Wait(int milliseconds = Timeout.Infinite)
	{
		if (milliseconds == Timeout.Infinite) Process.WaitForExit();
		else Process.WaitForExit(milliseconds);
		return await this;
	} */

        public Action<string> Log { get; set; }
	public Action<string> LogCommand { get; set; }
	public Action LogCommandEnd { get; set; }
	public Action<string> LogOutput { get; set; }
	public Action<string> LogError { get; set; }

	StringBuilder output, error, outputAndError;

	public Task<Shell> Task => CompletionSource.Task;

	public Shell Wait(int timeout = Timeout.Infinite)
	{
		if (Process != null)
		{
			if (timeout == Timeout.Infinite) Process.WaitForExit();
			else Process.WaitForExit(timeout);
			while (!CheckCompleted()) Thread.Sleep(0);
		}
		return this;
	}

	public Shell Wait(TimeSpan timeout) => Wait((int)timeout.TotalMilliseconds);

        public Shell WaitForInputIdle()
        {
            if (Process != null)
            {
                Process.WaitForInputIdle();
            }
            return this;
        }

        public async Task<string> Output()
	{
		if (Process == null && NotFound) return null;
		await this;
		await OutputLock.WaitAsync();
		try
		{
			return output.ToString();
		}
		finally
		{
			OutputLock.Release();
		}
	}
        public async Task<string> OutputAsync()
        {
            if (Process == null && NotFound) return null;
            await OutputLock.WaitAsync();
            try
            {
                return output.ToString();
            }
            finally
            {
                OutputLock.Release();
            }
        }

        public async Task<string> Error()
	{
		if (Process == null && NotFound) return null;
		await this;
		await ErrorLock.WaitAsync();
		try
		{
			return error.ToString();
		}
		finally
		{
			ErrorLock.Release();
		}
	}
        public async Task<string> ErrorAsync()
        {
            if (Process == null && NotFound) return null;
            await ErrorLock.WaitAsync();
            try
            {
                return error.ToString();
            }
            finally
            {
                ErrorLock.Release();
            }
        }

        public async Task<string> OutputAndError()
	{
		if (Process == null && NotFound) return null;
		await this;
		await OutputAndErrorLock.WaitAsync();
		try
		{
			return outputAndError.ToString();
		}
		finally
		{
			OutputAndErrorLock.Release();
		}
	}
        public async Task<string> OutputAndErrorAsync()
        {
            if (Process == null && NotFound) return null;
            await OutputAndErrorLock.WaitAsync();
            try
            {
                return outputAndError.ToString();
            }
            finally
            {
                OutputAndErrorLock.Release();
            }
        }

        public async Task<int> ExitCode()
	{
		if (Process == null && NotFound) return -500;
		await this;
		return exitCode;
	}
	public bool Redirect = false;
	public string LogFile = null;
	protected void AppendAllText(string filename, string text)
	{
		try
		{
			using (var file = new FileStream(filename, FileMode.Append, FileAccess.Write))
			using (var writer = new StreamWriter(file, Encoding.UTF8))
			{
				writer.Write(text);
			}
		}
		catch { }
	}
	protected virtual void OnLog(string text)
	{
		OutputAndErrorLock.Wait();
		try
		{
			outputAndError.Append(text);
			if (LogFile != null) AppendAllText(LogFile, text);
		}
		finally
		{
			OutputAndErrorLock.Release();
		}
	}

	protected virtual void OnLogCommand(string text)
	{
		OutputAndErrorLock.Wait();
		try
		{
			text = $"> {text}";
			if (Redirect) Console.WriteLine(text);
			if (LogFile != null) AppendAllText(LogFile, text + System.Environment.NewLine);
		}
		finally
		{
			OutputAndErrorLock.Release();
		}
	}
	protected virtual void OnLogCommandEnd()
	{
		OutputAndErrorLock.Wait();
		try
		{
			if (Redirect) Console.WriteLine();
			if (LogFile != null) AppendAllText(LogFile, System.Environment.NewLine);
		}
		finally
		{
			OutputAndErrorLock.Release();
		}
	}
	protected virtual void OnLogOutput(string text)
	{
		OutputLock.Wait();
		try
		{
			output.Append(text);
			if (Redirect) Console.Write(text);
		}
		finally
		{
			OutputLock.Release();
		}
	}
	protected virtual void OnLogError(string text)
	{
		ErrorLock.Wait();
		try
		{
			error.Append(text);
			if (Redirect) Console.Error.Write(text);
		}
		finally
		{
			ErrorLock.Release();
		}
	}
	public StreamWriter Input => Process?.StandardInput;

	static Shell standard = null;
	public static Shell Standard => standard ??= new StandardShell();

	public static Shell Default => Standard;
	public static bool IsWindows => OSInfo.IsWindows;
}

public class StandardShell : Shell
{
	public override string ShellExe => Shell.IsWindows ? "cmd" : "sh";
}