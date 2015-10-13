using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace StockSharp.Qsh2Bin
{

	class FilesTasker
	{
		private object _locker = new object();
		private List<FileInfoStruct> _files = new List<FileInfoStruct>();
		private List<string> _currentWorkSecurities = new List<string>();
		public void InitFiles(string path)
		{
			foreach (var filePath in Directory.GetFiles(path, "*.qsh"))
			{
				string fName = filePath;
				string onlyName = Path.GetFileName(fName);
				string secCode = onlyName.Substring(7, onlyName.Length - 22);
				DateTime fileDt = DateTime.Parse(onlyName.Substring(onlyName.Length - 14,10));
				_files.Add(new FileInfoStruct { FullFileName = fName, SecurityCode = secCode, fileDt = fileDt });				
			}
			foreach (var dir in Directory.GetDirectories(path))
			{
				InitFiles(dir);
			}
			_files = _files.OrderBy(el => el.fileDt).ToList();
		}

		public FileInfoStruct GetNextFile()
		{
			lock (_locker)
			{
				for (int i = 0; i < _files.Count; i++)
				{
					FileInfoStruct fis = _files[i];
					if (!_currentWorkSecurities.Contains(fis.SecurityCode))
					{
						_currentWorkSecurities.Add(fis.SecurityCode);
						_files.Remove(fis);
						return fis;
					}
					else
					{
						continue;
					}
				}
				if (_files.Count > 0)
				{
					return new FileInfoStruct { FullFileName = "wait" };
				}
				else
				{
					return new FileInfoStruct { FullFileName = "stop" };
				}
			}
		}

		public void ReportEndFileHandling(FileInfoStruct endedFile)
		{
			lock (_locker)
			{
				_currentWorkSecurities.Remove(endedFile.SecurityCode);
			}
		}
	}

	public struct FileInfoStruct
	{
		public string FullFileName;
		public string SecurityCode;
		public DateTime fileDt;
	}
}
