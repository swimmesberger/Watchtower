using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Proxy.Handlers;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>JSON serializer context for Proxy module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RouteDto))]
[JsonSerializable(typeof(ListRoutes.Query), TypeInfoPropertyName = "ListRoutesQuery")]
[JsonSerializable(typeof(ListRoutes.Response), TypeInfoPropertyName = "ListRoutesResponse")]
[JsonSerializable(typeof(GetRoute.Query), TypeInfoPropertyName = "GetRouteQuery")]
[JsonSerializable(typeof(GetRoute.Response), TypeInfoPropertyName = "GetRouteResponse")]
[JsonSerializable(typeof(CreateRoute.Command), TypeInfoPropertyName = "CreateRouteCommand")]
[JsonSerializable(typeof(CreateRoute.Response), TypeInfoPropertyName = "CreateRouteResponse")]
[JsonSerializable(typeof(UpdateRoute.Command), TypeInfoPropertyName = "UpdateRouteCommand")]
[JsonSerializable(typeof(UpdateRoute.Response), TypeInfoPropertyName = "UpdateRouteResponse")]
[JsonSerializable(typeof(DeleteRoute.Command), TypeInfoPropertyName = "DeleteRouteCommand")]
[JsonSerializable(typeof(DeleteRoute.Response), TypeInfoPropertyName = "DeleteRouteResponse")]
[JsonSerializable(typeof(CheckDns.Command), TypeInfoPropertyName = "CheckDnsCommand")]
[JsonSerializable(typeof(CheckDns.Response), TypeInfoPropertyName = "CheckDnsResponse")]
[JsonSerializable(typeof(GetProxyStatus.Query), TypeInfoPropertyName = "GetProxyStatusQuery")]
[JsonSerializable(typeof(GetProxyStatus.Response), TypeInfoPropertyName = "GetProxyStatusResponse")]
[JsonSerializable(typeof(GetAccess.Query), TypeInfoPropertyName = "GetAccessQuery")]
[JsonSerializable(typeof(GetAccess.Response), TypeInfoPropertyName = "GetAccessResponse")]
[JsonSerializable(typeof(SetAccess.Command), TypeInfoPropertyName = "SetAccessCommand")]
[JsonSerializable(typeof(SetAccess.Response), TypeInfoPropertyName = "SetAccessResponse")]
[JsonSerializable(typeof(CertificateDto))]
[JsonSerializable(typeof(ListCertificates.Query), TypeInfoPropertyName = "ListCertificatesQuery")]
[JsonSerializable(typeof(ListCertificates.Response), TypeInfoPropertyName = "ListCertificatesResponse")]
[JsonSerializable(typeof(RenewCertificate.Command), TypeInfoPropertyName = "RenewCertificateCommand")]
[JsonSerializable(typeof(RenewCertificate.Response), TypeInfoPropertyName = "RenewCertificateResponse")]
[JsonSerializable(typeof(ProxyConfigDto))]
[JsonSerializable(typeof(ProxyYarpConfigDto))]
[JsonSerializable(typeof(ProxyCloudflareConfigDto))]
[JsonSerializable(typeof(GetProxyConfig.Query), TypeInfoPropertyName = "GetProxyConfigQuery")]
[JsonSerializable(typeof(GetProxyConfig.Response), TypeInfoPropertyName = "GetProxyConfigResponse")]
[JsonSerializable(typeof(UpdateProxyConfig.Command), TypeInfoPropertyName = "UpdateProxyConfigCommand")]
[JsonSerializable(typeof(UpdateProxyConfig.Response), TypeInfoPropertyName = "UpdateProxyConfigResponse")]
[JsonSerializable(typeof(ListCloudflareForeignRoutes.ForeignRouteDto))]
[JsonSerializable(typeof(ListCloudflareForeignRoutes.Query), TypeInfoPropertyName = "ListCloudflareForeignRoutesQuery")]
[JsonSerializable(typeof(ListCloudflareForeignRoutes.Response), TypeInfoPropertyName = "ListCloudflareForeignRoutesResponse")]
public sealed partial class ProxyJsonContext : JsonSerializerContext;
