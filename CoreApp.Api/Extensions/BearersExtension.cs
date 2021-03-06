﻿using CoreApp.Api.Options.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace CoreApp.Api.Extensions
{
    public static class BearersExtension
    {
        /// <summary>
        ///     Setup OpenIddict Configuration
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static async Task OpenIdInitializeAsync(IServiceProvider services)
        {
            // Create a new service scope to ensure the database context is correctly disposed when this methods returns.
            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<DbContext>();
            await context.Database.EnsureCreatedAsync();

            var manager = scope.ServiceProvider
                .GetRequiredService<OpenIddictApplicationManager<OpenIddictApplication>>();

            var openIdOptions = scope.ServiceProvider
                .GetRequiredService<OidcAuthorizationServerOptions>();

            foreach (var client in openIdOptions.Clients)
            {
                foreach (var descriptor in client.ApplicationDescriptors)
                {
                    descriptor.ClientId = client.ClientId;
                    descriptor.ClientSecret = client.ClientSecret;

                    if (await manager.FindByClientIdAsync(client.ClientId) != null)
                        continue;

                    await manager.CreateAsync(descriptor);
                }
            }
        }

        public static void AddBearers
        (
            this IServiceCollection services,
            IWebHostEnvironment environment,
            OidcAuthorizationServerOptions openIdOptions,
            AuthenticationOptions authenticationOptions,
            string[] schemes
        )
        {
            services
                .AddDbContext<DbContext>(options =>
                {
                    // Configure the context to use an in-memory store.
                    options.UseInMemoryDatabase(nameof(DbContext));

                    // Register the entity sets needed by OpenIddict.
                    // Note: use the generic overload if you need
                    // to replace the default OpenIddict entities.
                    options.UseOpenIddict();
                })
                .AddOpenIddict()
                .AddCore(options =>
                {
                    // Configure OpenIddict to use the Entity Framework Core stores and entities.
                    options.UseEntityFrameworkCore().UseDbContext<DbContext>();
                })
                .AddServer(options =>
                {
                    // Register the ASP.NET Core MVC binder used by OpenIddict.
                    // Note: if you don't call this method, you won't be able to
                    // bind OpenIdConnectRequest or OpenIdConnectResponse parameters.
                    options.UseMvc();

                    // Enable the authorization/token endpoints (required to use the code flow).
                    options.EnableTokenEndpoint(@$"/{authenticationOptions.TokenEndpoint
                            .Replace(authenticationOptions.Issuer, string.Empty)}");
                    //.EnableAuthorizationEndpoint("/connect/authorize");

                    // Allow client applications to use the grant_type=client_credentials flow.
                    options.AllowClientCredentialsFlow();

                    // During development, you can disable the HTTPS requirement.
                    if (environment.IsDevelopment())
                        options.DisableHttpsRequirement();

                    // Accept token requests that don't specify a client_id.
                    // options.AcceptAnonymousClients();

                    options.EnableRequestCaching();

                    // Note: to use JWT access tokens instead of the default
                    // encrypted format, the following lines are required:
                    //
                    options.UseJsonWebTokens();

                    // Register a new ephemeral key, that is discarded when the application
                    // shuts down. Tokens signed using this key are automatically invalidated.
                    // This method should only be used during development.
                    //options.AddEphemeralSigningKey();

                    // On production, using a X.509 certificate stored in the machine store is recommended.
                    options.AddSigningCertificate(LoadCertificate(openIdOptions));

                    var expiryInSeconds = openIdOptions.AccessTokenExpiration;
                    options.SetAccessTokenLifetime(TimeSpan.FromSeconds(Convert.ToDouble(expiryInSeconds)));

                    // Note: if you don't want to use permissions, you can disable
                    // permission enforcement by uncommenting the following lines:
                    //
                    // options.IgnoreEndpointPermissions()
                    //        .IgnoreGrantTypePermissions()
                    //        .IgnoreScopePermissions();
                });

            services
                .AddAuthentication(schemes[0])
                .AddJwtBearer(schemes[0], options =>
                {
                    options.Authority = authenticationOptions.Issuer;
                    options.Audience = authenticationOptions.Audience;
                    options.RequireHttpsMetadata = !environment.IsDevelopment();
                    options.IncludeErrorDetails = environment.IsDevelopment(); // if it is not development sets to false
                    options.SaveToken = true;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateLifetime = true,
                        ValidateIssuer = true,
                        ValidIssuer = authenticationOptions.Issuer,
                        ValidateAudience = true,
                        ValidAudience = authenticationOptions.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new X509SecurityKey(LoadCertificate(openIdOptions))
                    };
                })
                .AddJwtBearer(schemes[1], options =>
                {
                    options.IncludeErrorDetails = true;
                    //options.Authority = appSettings.AdfsAuthority;
                    //options.Audience = appSettings.AdfsAudience;
                    //options.MetadataAddress = appSettings.AdfsMetadataAddress;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        //ValidIssuer = appSettings.AdfsIssuer,
                        //ValidAudience = appSettings.AdfsAudience,
                        ValidateLifetime = true,
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true
                    };
                });
        }

        public static X509Certificate2 LoadCertificate(OidcAuthorizationServerOptions openIdOptions)
        {
            CertificateOptions options = openIdOptions.SigningCertificate;

            if (options != null)
            {
                if (options.Store != null && options.Location != null)
                {
                    using (var store = new X509Store(options.Store,
                        (StoreLocation)Enum.Parse(typeof(StoreLocation), options.Location)))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        var certificate = store.Certificates.Find(
                            X509FindType.FindBySubjectName,
                            options.Subject,
                            validOnly: !options.AllowInvalid);

                        if (certificate.Count == 0)
                            throw new InvalidOperationException($"Certificate not found for {options.Subject}.");

                        return certificate[0];
                    }
                }
            }

            throw new InvalidOperationException("No valid certificate configuration found for the current endpoint.");
        }

    }
}
