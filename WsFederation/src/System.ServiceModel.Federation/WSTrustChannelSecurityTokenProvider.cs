// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable 1591

using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.IO;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;
using System.ServiceModel.Security.Tokens;
using System.Text;
using System.Xml;
using Microsoft.IdentityModel.Protocols.WsAddressing;
using Microsoft.IdentityModel.Protocols.WsPolicy;
using Microsoft.IdentityModel.Protocols.WsTrust;
using Microsoft.IdentityModel.Tokens.Saml2;

namespace System.ServiceModel.Federation
{

    /// <summary>
    /// Custom WSTrustChannelSecurityTokenProvider that returns a SAML assertion
    /// </summary>
    public class WSTrustChannelSecurityTokenProvider : SecurityTokenProvider
    {
        public WSTrustChannelSecurityTokenProvider(SecurityTokenRequirement tokenRequirement)
        {
            SecurityTokenRequirement = tokenRequirement ?? throw new ArgumentNullException(nameof(tokenRequirement));
        }

        public SecurityTokenRequirement SecurityTokenRequirement
        {
            get;
        }

        /// <summary>
        /// Calls out to the STS, if necessary to get a token
        /// </summary>
        protected override SecurityToken GetTokenCore(TimeSpan timeout)
        {
            // Send WsTrust messge to STS
            var issuedTokenParameters = SecurityTokenRequirement.GetProperty<IssuedSecurityTokenParameters>("http://schemas.microsoft.com/ws/2006/05/servicemodel/securitytokenrequirement/IssuedSecurityTokenParameters");
            var target = SecurityTokenRequirement.GetProperty<EndpointAddress>("http://schemas.microsoft.com/ws/2006/05/servicemodel/securitytokenrequirement/TargetAddress");
            var wsTrustRequest = new WsTrustRequest()
            {
                AppliesTo = new AppliesTo(new EndpointReference(target.Uri.OriginalString)),
                Context = Guid.NewGuid().ToString(),
                // hard coded, as WCF does not support Bearer right now.
                KeyType = WsTrustKeyTypes.Trust13.Bearer,
                RequestType = WsTrustConstants.Trust13.WsTrustActions.Issue,
                TokenType = SecurityTokenRequirement.TokenType
            };

            WsTrustResponse trustResponse = null;
            using (var memeoryStream = new MemoryStream())
            {
                var writer = XmlDictionaryWriter.CreateTextWriter(memeoryStream, Encoding.UTF8);
                var serializer = new WsTrustSerializer();
                serializer.WriteRequest(writer, WsTrustVersion.Trust13, wsTrustRequest);
                writer.Flush();
                var reader = XmlDictionaryReader.CreateTextReader(memeoryStream.ToArray(), XmlDictionaryReaderQuotas.Max);
                var factory = new ChannelFactory<IRequestChannel>(issuedTokenParameters.IssuerBinding, issuedTokenParameters.IssuerAddress);

                // Temporary as test STS is not trusted.
                // This code should be removed.
                factory.Credentials.ServiceCertificate.Authentication.CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None;
                factory.Credentials.ServiceCertificate.SslCertificateAuthentication = new X509ServiceCertificateAuthentication();
                factory.Credentials.ServiceCertificate.SslCertificateAuthentication.CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None;

                var channel = factory.CreateChannel();
                var reply = channel.Request(Message.CreateMessage(MessageVersion.Soap12WSAddressing10, WsTrustActions.Trust13.IssueRequest, reader));
                trustResponse = serializer.ReadResponse(reply.GetReaderAtBodyContents());
            }

            // Create GenericXmlSecurityToken
            // Assumes that token is first and Saml2SecurityToken.
            using (var stream = new MemoryStream())
            {
                var writer = XmlDictionaryWriter.CreateTextWriter(stream, Encoding.UTF8, false);
                var tokenHandler = new Saml2SecurityTokenHandler();
                tokenHandler.TryWriteSourceData(writer, trustResponse.RequestSecurityTokenResponseCollection[0].RequestedSecurityToken.SecurityToken);
                writer.Flush();
                stream.Seek(0, SeekOrigin.Begin);
                var dom = new XmlDocument
                {
                    PreserveWhitespace = true
                };

                dom.Load(new XmlTextReader(stream) { DtdProcessing = DtdProcessing.Prohibit });
                return new GenericXmlSecurityToken(dom.DocumentElement,
                                                    null,
                                                    DateTime.UtcNow,
                                                    DateTime.UtcNow + TimeSpan.FromDays(1),
                                                    new Saml2AssertionKeyIdentifierClause(trustResponse.RequestSecurityTokenResponseCollection[0].AttachedReference.Id),
                                                    new Saml2AssertionKeyIdentifierClause(trustResponse.RequestSecurityTokenResponseCollection[0].UnattachedReference.Id),
                                                    null);
            }
        }
    }
}
