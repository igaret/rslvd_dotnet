﻿/*
Technitium DNS Server
Copyright (C) 2025  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.ApplicationCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace DnsServerCore.Dns.Applications
{
    public sealed class DnsApplicationManager : IDisposable
    {
        #region variables

        readonly DnsServer _dnsServer;

        readonly string _appsPath;

        readonly ConcurrentDictionary<string, DnsApplication> _applications = new ConcurrentDictionary<string, DnsApplication>();

        IReadOnlyList<IDnsRequestController> _dnsRequestControllers = Array.Empty<IDnsRequestController>();
        IReadOnlyList<IDnsAuthoritativeRequestHandler> _dnsAuthoritativeRequestHandlers = Array.Empty<IDnsAuthoritativeRequestHandler>();
        IReadOnlyList<IDnsRequestBlockingHandler> _dnsRequestBlockingHandlers = Array.Empty<IDnsRequestBlockingHandler>();
        IReadOnlyList<IDnsQueryLogger> _dnsQueryLoggers = Array.Empty<IDnsQueryLogger>();
        IReadOnlyList<IDnsPostProcessor> _dnsPostProcessors = Array.Empty<IDnsPostProcessor>();

        #endregion

        #region constructor

        public DnsApplicationManager(DnsServer dnsServer)
        {
            _dnsServer = dnsServer;

            _appsPath = Path.Combine(_dnsServer.ConfigFolder, "apps");

            if (!Directory.Exists(_appsPath))
                Directory.CreateDirectory(_appsPath);
        }

        #endregion

        #region IDisposable

        bool _disposed;

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_applications != null)
                    UnloadAllApplications();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region private

        private async Task<DnsApplication> LoadApplicationAsync(string applicationFolder, bool refreshAppObjectList)
        {
            string applicationName = Path.GetFileName(applicationFolder);

            DnsApplication application = new DnsApplication(new DnsServerInternal(_dnsServer, applicationName, applicationFolder), applicationName);

            await application.InitializeAsync();

            if (!_applications.TryAdd(application.Name, application))
            {
                application.Dispose();
                throw new DnsServerException("DNS application already exists: " + application.Name);
            }

            application.ConfigUpdated += Application_ConfigUpdated;

            if (refreshAppObjectList)
                RefreshAppObjectLists();

            return application;
        }

        private void UnloadApplication(string applicationName)
        {
            if (!_applications.TryRemove(applicationName, out DnsApplication removedApp))
                throw new DnsServerException("DNS application does not exists: " + applicationName);

            RefreshAppObjectLists();

            removedApp.ConfigUpdated -= Application_ConfigUpdated;
            removedApp.Dispose();
        }

        private void Application_ConfigUpdated(object sender, EventArgs e)
        {
            //refresh app objects to allow sorting them as per app preference
            RefreshAppObjectLists();
        }

        private void RefreshAppObjectLists()
        {
            List<IDnsRequestController> dnsRequestControllers = new List<IDnsRequestController>(1);
            List<IDnsAuthoritativeRequestHandler> dnsAuthoritativeRequestHandlers = new List<IDnsAuthoritativeRequestHandler>(1);
            List<IDnsRequestBlockingHandler> dnsRequestBlockingHandlers = new List<IDnsRequestBlockingHandler>(1);
            List<IDnsQueryLogger> dnsQueryLoggers = new List<IDnsQueryLogger>(1);
            List<IDnsPostProcessor> dnsPostProcessors = new List<IDnsPostProcessor>(1);

            foreach (KeyValuePair<string, DnsApplication> application in _applications)
            {
                foreach (KeyValuePair<string, IDnsRequestController> controller in application.Value.DnsRequestControllers)
                    dnsRequestControllers.Add(controller.Value);

                foreach (KeyValuePair<string, IDnsAuthoritativeRequestHandler> handler in application.Value.DnsAuthoritativeRequestHandlers)
                    dnsAuthoritativeRequestHandlers.Add(handler.Value);

                foreach (KeyValuePair<string, IDnsRequestBlockingHandler> blocker in application.Value.DnsRequestBlockingHandler)
                    dnsRequestBlockingHandlers.Add(blocker.Value);

                foreach (KeyValuePair<string, IDnsQueryLogger> logger in application.Value.DnsQueryLoggers)
                    dnsQueryLoggers.Add(logger.Value);

                foreach (KeyValuePair<string, IDnsPostProcessor> processor in application.Value.DnsPostProcessors)
                    dnsPostProcessors.Add(processor.Value);
            }

            //sort app objects by preference
            dnsRequestControllers.Sort(CompareApps);
            dnsAuthoritativeRequestHandlers.Sort(CompareApps);
            dnsRequestBlockingHandlers.Sort(CompareApps);
            dnsQueryLoggers.Sort(CompareApps);
            dnsPostProcessors.Sort(CompareApps);

            _dnsRequestControllers = dnsRequestControllers;
            _dnsAuthoritativeRequestHandlers = dnsAuthoritativeRequestHandlers;
            _dnsRequestBlockingHandlers = dnsRequestBlockingHandlers;
            _dnsQueryLoggers = dnsQueryLoggers;
            _dnsPostProcessors = dnsPostProcessors;
        }

        private static int CompareApps<T>(T x, T y)
        {
            int xp;
            int yp;

            if (x is IDnsApplicationPreference xpref)
                xp = xpref.Preference;
            else
                xp = 100;

            if (y is IDnsApplicationPreference ypref)
                yp = ypref.Preference;
            else
                yp = 100;

            return xp.CompareTo(yp);
        }

        #endregion

        #region public

        public void UnloadAllApplications()
        {
            foreach (KeyValuePair<string, DnsApplication> application in _applications)
            {
                try
                {
                    application.Value.Dispose();
                }
                catch (Exception ex)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write(ex);
                }
            }

            _applications.Clear();
            _dnsRequestControllers = Array.Empty<IDnsRequestController>();
            _dnsAuthoritativeRequestHandlers = Array.Empty<IDnsAuthoritativeRequestHandler>();
            _dnsRequestBlockingHandlers = Array.Empty<IDnsRequestBlockingHandler>();
            _dnsQueryLoggers = Array.Empty<IDnsQueryLogger>();
            _dnsPostProcessors = Array.Empty<IDnsPostProcessor>();
        }

        public async Task LoadAllApplicationsAsync()
        {
            UnloadAllApplications();

            List<Task> tasks = new List<Task>();

            foreach (string applicationFolder in Directory.GetDirectories(_appsPath))
            {
                tasks.Add(Task.Run(async delegate ()
                {
                    try
                    {
                        _dnsServer.LogManager?.Write("DNS Server is loading DNS application: " + Path.GetFileName(applicationFolder));

                        _ = await LoadApplicationAsync(applicationFolder, false);

                        _dnsServer.LogManager?.Write("DNS Server successfully loaded DNS application: " + Path.GetFileName(applicationFolder));
                    }
                    catch (Exception ex)
                    {
                        _dnsServer.LogManager?.Write("DNS Server failed to load DNS application: " + Path.GetFileName(applicationFolder) + "\r\n" + ex.ToString());
                    }
                }));
            }

            await Task.WhenAll(tasks);

            RefreshAppObjectLists();
        }

        public async Task<DnsApplication> InstallApplicationAsync(string applicationName, Stream appStream)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                if (applicationName.Contains(invalidChar))
                    throw new DnsServerException("The application name contains an invalid character: " + invalidChar);
            }

            if (_applications.ContainsKey(applicationName))
                throw new DnsServerException("DNS application already exists: " + applicationName);

            using (ZipArchive appZip = new ZipArchive(appStream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                string applicationFolder = Path.Combine(_appsPath, applicationName);

                if (Directory.Exists(applicationFolder))
                    Directory.Delete(applicationFolder, true);

                try
                {
                    appZip.ExtractToDirectory(applicationFolder, true);

                    return await LoadApplicationAsync(applicationFolder, true);
                }
                catch
                {
                    if (Directory.Exists(applicationFolder))
                        Directory.Delete(applicationFolder, true);

                    throw;
                }
            }
        }

        public async Task<DnsApplication> UpdateApplicationAsync(string applicationName, Stream appStream)
        {
            if (!_applications.ContainsKey(applicationName))
                throw new DnsServerException("DNS application does not exists: " + applicationName);

            using (ZipArchive appZip = new ZipArchive(appStream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                UnloadApplication(applicationName);

                string applicationFolder = Path.Combine(_appsPath, applicationName);

                foreach (ZipArchiveEntry entry in appZip.Entries)
                {
                    string entryPath = entry.FullName;

                    if (Path.DirectorySeparatorChar != '/')
                        entryPath = entryPath.Replace('/', '\\');

                    string filePath = Path.Combine(applicationFolder, entryPath);

                    if ((entry.Name == "dnsApp.config") && File.Exists(filePath))
                        continue; //avoid overwriting existing config file

                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                    entry.ExtractToFile(filePath, true);
                }

                return await LoadApplicationAsync(applicationFolder, true);
            }
        }

        public void UninstallApplication(string applicationName)
        {
            if (_applications.TryRemove(applicationName, out DnsApplication removedApp))
            {
                RefreshAppObjectLists();

                removedApp.ConfigUpdated -= Application_ConfigUpdated;
                removedApp.Dispose();

                if (Directory.Exists(removedApp.DnsServer.ApplicationFolder))
                {
                    try
                    {
                        Directory.Delete(removedApp.DnsServer.ApplicationFolder, true);
                    }
                    catch (Exception ex)
                    {
                        _dnsServer.LogManager?.Write(ex);
                    }
                }
            }
        }

        #endregion

        #region properties

        public IReadOnlyDictionary<string, DnsApplication> Applications
        { get { return _applications; } }

        public IReadOnlyList<IDnsRequestController> DnsRequestControllers
        { get { return _dnsRequestControllers; } }

        public IReadOnlyList<IDnsAuthoritativeRequestHandler> DnsAuthoritativeRequestHandlers
        { get { return _dnsAuthoritativeRequestHandlers; } }

        public IReadOnlyList<IDnsRequestBlockingHandler> DnsRequestBlockingHandlers
        { get { return _dnsRequestBlockingHandlers; } }

        public IReadOnlyList<IDnsQueryLogger> DnsQueryLoggers
        { get { return _dnsQueryLoggers; } }

        public IReadOnlyList<IDnsPostProcessor> DnsPostProcessors
        { get { return _dnsPostProcessors; } }

        #endregion
    }
}
