﻿using System;
using System.Collections.Generic;
using System.IO;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using Pipelines = GitHub.DistributedTask.Pipelines;
using System.Collections.ObjectModel;
using System.Linq;

namespace GitHub.Runner.Worker.Container
{
    public class ContainerInfo
    {
        private List<MountVolume> _mountVolumes;
        private IDictionary<string, string> _userPortMappings;
        private List<PortMapping> _portMappings;
        private IDictionary<string, string> _environmentVariables;
        private List<PathMapping> _pathMappings = new();
        private Dictionary<string, List<PathMapping>> _osPathMappings = new Dictionary<string, List<PathMapping>>{ { "linux", new List<PathMapping>() }, { "windows", new List<PathMapping>() } };

        public ContainerInfo()
        {
        }

        public ContainerInfo(IHostContext hostContext)
        {
            UpdateWebProxyEnv(hostContext.WebProxy);
        }

        public ContainerInfo(IHostContext hostContext, Pipelines.JobContainer container, bool isJobContainer = true, string networkAlias = null)
        {
            this.ContainerName = container.Alias;

            string containerImage = container.Image;
            ArgUtil.NotNullOrEmpty(containerImage, nameof(containerImage));

            this.ContainerImage = containerImage;
            this.ContainerDisplayName = $"{container.Alias}_{Pipelines.Validation.NameValidation.Sanitize(containerImage)}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            this.ContainerCreateOptions = container.Options;
            _environmentVariables = container.Environment;
            this.IsJobContainer = isJobContainer;
            this.ContainerNetworkAlias = networkAlias;
            this.RegistryAuthUsername = container.Credentials?.Username;
            this.RegistryAuthPassword = container.Credentials?.Password;
            this.RegistryServer = DockerUtil.ParseRegistryHostnameFromImageName(this.ContainerImage);


            _osPathMappings["windows"].Add(new PathMapping(hostContext.GetDirectory(WellKnownDirectory.Work), "C:\\__w"));
            _osPathMappings["windows"].Add(new PathMapping(Path.GetDirectoryName(hostContext.GetDirectory(WellKnownDirectory.Tools)), "C:\\__t")); // Tool cache folder may come from ENV, so we need a unique folder to avoid collision
            _osPathMappings["windows"].Add(new PathMapping(hostContext.GetDirectory(WellKnownDirectory.Externals), "C:\\__e"));
            _osPathMappings["linux"].Add(new PathMapping(hostContext.GetDirectory(WellKnownDirectory.Work), "/__w"));
            _osPathMappings["linux"].Add(new PathMapping(Path.GetDirectoryName(hostContext.GetDirectory(WellKnownDirectory.Tools)), "/__t")); // Tool cache folder may come from ENV, so we need a unique folder to avoid collision
            _osPathMappings["linux"].Add(new PathMapping(hostContext.GetDirectory(WellKnownDirectory.Externals), "/__e"));

            if (container.Ports?.Count > 0)
            {
                foreach (var port in container.Ports)
                {
                    UserPortMappings[port] = port;
                }
            }
            if (container.Volumes?.Count > 0)
            {
                foreach (var volume in container.Volumes)
                {
                    MountVolumes.Add(new MountVolume(volume, isUserProvided: true));
                }
            }

            UpdateWebProxyEnv(hostContext.WebProxy);
        }

        public string ContainerId { get; set; }
        public string ContainerDisplayName { get; set; }
        public string ContainerNetwork { get; set; }
        public string ContainerNetworkAlias { get; set; }
        public string ContainerImage { get; set; }
        public string ContainerName { get; set; }
        public string ContainerEntryPointArgs { get; set; }
        public string ContainerEntryPoint { get; set; }
        public string ContainerWorkDirectory { get; set; }
        public string ContainerCreateOptions { get; private set; }
        public string ContainerRuntimePath { get; set; }
        public string RegistryServer { get; set; }
        public string RegistryAuthUsername { get; set; }
        public string RegistryAuthPassword { get; set; }
        public bool IsJobContainer { get; set; }
        public bool IsAlpine { get; set; }

        public bool FailedInitialization { get; set; }

        public IDictionary<string, string> ContainerEnvironmentVariables
        {
            get
            {
                if (_environmentVariables == null)
                {
                    _environmentVariables = new Dictionary<string, string>();
                }

                return _environmentVariables;
            }
        }
        public ReadOnlyCollection<MountVolume> UserMountVolumes
        {
            get
            {
                return MountVolumes.Where(v => !string.IsNullOrEmpty(v.UserProvidedValue)).ToList().AsReadOnly();
            }
        }
        public ReadOnlyCollection<MountVolume> SystemMountVolumes
        {
            get
            {
                return MountVolumes.Where(v => string.IsNullOrEmpty(v.UserProvidedValue)).ToList().AsReadOnly();
            }
        }
        public List<MountVolume> MountVolumes
        {
            get
            {
                if (_mountVolumes == null)
                {
                    _mountVolumes = new List<MountVolume>();
                }

                return _mountVolumes;
            }
        }

        public IDictionary<string, string> UserPortMappings
        {
            get
            {
                if (_userPortMappings == null)
                {
                    _userPortMappings = new Dictionary<string, string>();
                }

                return _userPortMappings;
            }
        }

        public List<PortMapping> PortMappings
        {
            get
            {
                if (_portMappings == null)
                {
                    _portMappings = new List<PortMapping>();
                }

                return _portMappings;
            }
        }

        public string Os { get; internal set; }
        public string Arch { get; internal set; }
        public string TranslateToContainerPath(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var mapping in _pathMappings.Concat(_osPathMappings[Os ?? "linux"]))
                {
                    if(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) {
                        var hostPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        var mHostPath = mapping.HostPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        if (string.Equals(hostPath, mHostPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return mapping.ContainerPath;
                        }

                        if (hostPath.StartsWith(mHostPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            return mapping.ContainerPath + hostPath.Remove(0, mHostPath.Length).Replace(Os == "windows" ? Path.AltDirectorySeparatorChar : Path.DirectorySeparatorChar, Os == "windows" ? Path.DirectorySeparatorChar : Path.AltDirectorySeparatorChar);
                        }
                    } else {
                        if (string.Equals(path, mapping.HostPath))
                        {
                            return mapping.ContainerPath;
                        }

                        if (path.StartsWith(mapping.HostPath + Path.DirectorySeparatorChar))
                        {
                            return mapping.ContainerPath + path.Remove(0, mapping.HostPath.Length);
                        }
                    }
                }
            }

            return path;
        }

        public string TranslateToHostPath(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var mapping in _pathMappings.Concat(_osPathMappings[Os ?? "linux"]))
                {
                    if(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) {
                        var comp = Os == "windows" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        var containerPath = Os == "windows" ? path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) : path;
                        var mContainerPath = Os == "windows" ? mapping.ContainerPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) : mapping.ContainerPath;
                        if (string.Equals(containerPath, mContainerPath, comp))
                        {
                            return mapping.HostPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        }

                        if (Os == "windows" ? containerPath.StartsWith(mContainerPath + Path.DirectorySeparatorChar, comp) :
                            containerPath.StartsWith(mContainerPath + Path.AltDirectorySeparatorChar, comp))
                        {
                            return (mapping.HostPath + containerPath.Remove(0, mContainerPath.Length)).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        }
                    } else {
                        if (string.Equals(path, mapping.ContainerPath))
                        {
                            return mapping.HostPath;
                        }

                        if (path.StartsWith(mapping.ContainerPath + Path.DirectorySeparatorChar))
                        {
                            return mapping.HostPath + path.Remove(0, mapping.ContainerPath.Length);
                        }
                    }
                }
            }

            return path;
        }

        public void AddPortMappings(List<PortMapping> portMappings)
        {
            foreach (var port in portMappings)
            {
                PortMappings.Add(port);
            }
        }

        public void AddPortMappings(IDictionary<string, string> portMappings)
        {
            foreach (var pair in portMappings)
            {
                PortMappings.Add(new PortMapping(pair.Key, pair.Value));
            }
        }

        public void AddPathTranslateMapping(string hostCommonPath, string containerCommonPath)
        {
            _pathMappings.Insert(0, new PathMapping(hostCommonPath, containerCommonPath));
        }

        private void UpdateWebProxyEnv(RunnerWebProxy webProxy)
        {
            // Set common forms of proxy variables if configured in Runner and not set directly by container.env
            if (!String.IsNullOrEmpty(webProxy.HttpProxyAddress))
            {
                ContainerEnvironmentVariables.TryAdd("HTTP_PROXY", webProxy.HttpProxyAddress);
                ContainerEnvironmentVariables.TryAdd("http_proxy", webProxy.HttpProxyAddress);
            }
            if (!String.IsNullOrEmpty(webProxy.HttpsProxyAddress))
            {
                ContainerEnvironmentVariables.TryAdd("HTTPS_PROXY", webProxy.HttpsProxyAddress);
                ContainerEnvironmentVariables.TryAdd("https_proxy", webProxy.HttpsProxyAddress);
            }
            if (!String.IsNullOrEmpty(webProxy.NoProxyString))
            {
                ContainerEnvironmentVariables.TryAdd("NO_PROXY", webProxy.NoProxyString);
                ContainerEnvironmentVariables.TryAdd("no_proxy", webProxy.NoProxyString);
            }
        }
    }

    public class MountVolume
    {
        public string UserProvidedValue { get; set; }
        public MountVolume(string sourceVolumePath, string targetVolumePath, bool readOnly = false)
        {
            this.SourceVolumePath = sourceVolumePath;
            this.TargetVolumePath = targetVolumePath;
            this.ReadOnly = readOnly;
        }
        public MountVolume(string fromString)
        {
            ParseVolumeString(fromString);
        }

        public MountVolume(string fromString, bool isUserProvided)
        {
            ParseVolumeString(fromString);
            if (isUserProvided)
            {
                UserProvidedValue = fromString;
            }
        }

        private void ParseVolumeString(string volume)
        {
            var volumeSplit = volume.Split(":");
            if (volumeSplit.Length == 3)
            {
                // source:target:ro
                SourceVolumePath = volumeSplit[0];
                TargetVolumePath = volumeSplit[1];
                ReadOnly = String.Equals(volumeSplit[2], "ro", StringComparison.OrdinalIgnoreCase);
            }
            else if (volumeSplit.Length == 2)
            {
                if (String.Equals(volumeSplit[1], "ro", StringComparison.OrdinalIgnoreCase))
                {
                    // target:ro
                    TargetVolumePath = volumeSplit[0];
                    ReadOnly = true;
                }
                else
                {
                    // source:target
                    SourceVolumePath = volumeSplit[0];
                    TargetVolumePath = volumeSplit[1];
                    ReadOnly = false;
                }
            }
            else
            {
                // target - or, default to passing straight through
                TargetVolumePath = volume;
                ReadOnly = false;
            }
        }

        public string SourceVolumePath { get; set; }
        public string TargetVolumePath { get; set; }
        public bool ReadOnly { get; set; }
    }

    public class PortMapping
    {
        public PortMapping(string hostPort, string containerPort)
        {
            this.HostPort = hostPort;
            this.ContainerPort = containerPort;
        }

        public PortMapping(string hostPort, string containerPort, string protocol)
        {
            this.HostPort = hostPort;
            this.ContainerPort = containerPort;
            this.Protocol = protocol;
        }

        public string HostPort { get; set; }
        public string ContainerPort { get; set; }
        public string Protocol { get; set; }
    }

    public class DockerVersion
    {
        public DockerVersion(Version serverVersion, Version clientVersion)
        {
            this.ServerVersion = serverVersion;
            this.ClientVersion = clientVersion;
        }

        public Version ServerVersion { get; set; }
        public Version ClientVersion { get; set; }
    }

    public class PathMapping
    {
        public PathMapping(string hostPath, string containerPath)
        {
            this.HostPath = hostPath;
            this.ContainerPath = containerPath;
        }

        public string HostPath { get; set; }
        public string ContainerPath { get; set; }
    }
}
