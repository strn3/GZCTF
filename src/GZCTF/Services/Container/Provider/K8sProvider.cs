﻿using System.Text.Json;
using GZCTF.Models.Internal;
using GZCTF.Services.Interface;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GZCTF.Services.Container.Provider;

public class K8sMetadata : ContainerProviderMetadata
{
    /// <summary>
    /// 容器注册表鉴权 Secret 名称
    /// </summary>
    public string? AuthSecretName { get; set; }

    /// <summary>
    /// K8s 集群 Host IP
    /// </summary>
    public string HostIp { get; set; } = string.Empty;

    /// <summary>
    /// K8s 配置
    /// </summary>
    public K8sConfig Config { get; set; } = new();
}

public class K8sProvider : IContainerProvider<Kubernetes, K8sMetadata>
{
    const string NetworkPolicy = "gzctf-policy";
    readonly K8sMetadata _k8sMetadata;

    readonly Kubernetes _kubernetesClient;
    readonly IStringLocalizer<Program> _localizer;

    public K8sProvider(IOptions<RegistryConfig> registry, IOptions<ContainerProvider> options,
        ILogger<K8sProvider> logger, IStringLocalizer<Program> localizer)
    {
        _k8sMetadata = new()
        {
            Config = options.Value.K8sConfig ?? new(), PortMappingType = options.Value.PortMappingType, PublicEntry = options.Value.PublicEntry
        };
        _localizer = localizer;

        if (!File.Exists(_k8sMetadata.Config.KubeConfig))
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.ContainerProvider_K8sConfigLoadFailed),
                _k8sMetadata.Config.KubeConfig]);
            throw new FileNotFoundException(_k8sMetadata.Config.KubeConfig);
        }

        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(_k8sMetadata.Config.KubeConfig);

        _k8sMetadata.HostIp = config.Host[(config.Host.LastIndexOf('/') + 1)..config.Host.LastIndexOf(':')];

        _kubernetesClient = new Kubernetes(config);

        RegistryConfig registryValue = registry.Value;
        var withAuth = !string.IsNullOrWhiteSpace(registryValue.ServerAddress)
                       && !string.IsNullOrWhiteSpace(registryValue.UserName)
                       && !string.IsNullOrWhiteSpace(registryValue.Password);

        if (withAuth)
        {
            var padding = $"{registryValue.UserName}@{registryValue.Password}@{registryValue.ServerAddress}".ToMD5String();
            _k8sMetadata.AuthSecretName = $"{registryValue.UserName}-{padding}".ToValidRFC1123String("secret");
        }

        try
        {
            InitK8s(withAuth, registryValue);
        }
        catch (Exception e)
        {
            logger.LogError(e, Program.StaticLocalizer[nameof(Resources.Program.ContainerProvider_K8sInitFailed), config.Host]);
            Program.ExitWithFatalMessage(Program.StaticLocalizer[nameof(Resources.Program.ContainerProvider_K8sInitFailed), config.Host]);
        }

        logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.ContainerProvider_K8sInited), config.Host], TaskStatus.Success,
            LogLevel.Debug);
    }

    public Kubernetes GetProvider() => _kubernetesClient;

    public K8sMetadata GetMetadata() => _k8sMetadata;

    void InitK8s(bool withAuth, RegistryConfig? registry)
    {
        if (_kubernetesClient.CoreV1.ListNamespace().Items.All(ns => ns.Metadata.Name != _k8sMetadata.Config.Namespace))
            _kubernetesClient.CoreV1.CreateNamespace(
                new() { Metadata = new() { Name = _k8sMetadata.Config.Namespace } });

        if (_kubernetesClient.NetworkingV1.ListNamespacedNetworkPolicy(_k8sMetadata.Config.Namespace).Items
            .All(np => np.Metadata.Name != NetworkPolicy))
            _kubernetesClient.NetworkingV1.CreateNamespacedNetworkPolicy(new()
            {
                Metadata = new() { Name = NetworkPolicy },
                Spec = new()
                {
                    PodSelector = new(),
                    PolicyTypes = new[] { "Egress" },
                    Egress = new[]
                    {
                        new V1NetworkPolicyEgressRule
                        {
                            To = new[]
                            {
                                new V1NetworkPolicyPeer
                                {
                                    IpBlock = new()
                                    {
                                        Cidr = "0.0.0.0/0",
                                        Except = _k8sMetadata.Config.AllowCidr
                                    }
                                }
                            }
                        }
                    }
                }
            }, _k8sMetadata.Config.Namespace);

        if (withAuth && registry?.ServerAddress is not null)
        {
            var auth = Codec.Base64.Encode($"{registry.UserName}:{registry.Password}");
            var dockerJsonObj = new
            {
                auths = new Dictionary<string, object>
                {
                    { registry.ServerAddress, new { auth, username = registry.UserName, password = registry.Password } }
                }
            };
            var dockerJsonBytes = JsonSerializer.SerializeToUtf8Bytes(dockerJsonObj);
            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = _k8sMetadata.AuthSecretName, NamespaceProperty = _k8sMetadata.Config.Namespace },
                Data = new Dictionary<string, byte[]> { [".dockerconfigjson"] = dockerJsonBytes },
                Type = "kubernetes.io/dockerconfigjson"
            };

            try
            {
                _kubernetesClient.CoreV1.ReplaceNamespacedSecret(secret, _k8sMetadata.AuthSecretName,
                    _k8sMetadata.Config.Namespace);
            }
            catch
            {
                _kubernetesClient.CoreV1.CreateNamespacedSecret(secret, _k8sMetadata.Config.Namespace);
            }
        }
    }
}