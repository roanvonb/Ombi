﻿using System;
using System.IO;
using System.Security.Principal;
using AutoMapper;
using AutoMapper.EquivalencyExpression;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using Ombi.Auth;
using Ombi.Config;
using Ombi.DependencyInjection;
using Ombi.Mapping;
using Ombi.Schedule;
using Serilog;
using Serilog.Events;
using Swashbuckle.AspNetCore.Swagger;

namespace Ombi
{
    public partial class Startup
    {
        public Startup(IHostingEnvironment env)
        {
            Console.WriteLine(env.ContentRootPath);
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", false, true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true)
                .AddEnvironmentVariables();
            Configuration = builder.Build();

            if (env.IsDevelopment())
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.RollingFile(Path.Combine(env.ContentRootPath, "Logs", "log-{Date}.txt"))
                    .WriteTo.SQLite("Ombi.db", "Logs", LogEventLevel.Debug)
                    .CreateLogger();
            }
            if (env.IsProduction())
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.RollingFile(Path.Combine(env.ContentRootPath, "Logs", "log-{Date}.txt"))
                    .WriteTo.SQLite("Ombi.db", "Logs", LogEventLevel.Debug)
                    .CreateLogger();
            }
        }

        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add framework services.
            services.AddMemoryCache();
            services.AddMvc();
            services.AddOmbiMappingProfile();
            services.AddAutoMapper(expression =>
            {
                expression.AddCollectionMappers();
            });
            services.RegisterDependencies(); // Ioc and EF
            services.AddSwaggerGen(c =>
            {
                c.DescribeAllEnumsAsStrings();
                c.SwaggerDoc("v1", new Info
                {
                    Version = "v1",
                    Title = "Ombi Api",
                    Description = "The API for Ombi, most of these calls require an auth token that you can get from calling POST:\"api/v1/token/\" with the body of: \n {\n\"username\":\"YOURUSERNAME\",\n\"password\":\"YOURPASSWORD\"\n} \n" +
                                  "You can then use the returned token in the JWT Token field e.g. \"Bearer Token123xxff\"",
                    Contact = new Contact
                    {
                        Email = "tidusjar@gmail.com",
                        Name = "Jamie Rees",
                        Url = "https://www.ombi.io/"
                    }
                });
                c.CustomSchemaIds(x => x.FullName);
                var basePath = PlatformServices.Default.Application.ApplicationBasePath;
                var xmlPath = Path.Combine(basePath, "Swagger.xml");
                c.IncludeXmlComments(xmlPath);
                
                c.AddSecurityDefinition("Authentication",new ApiKeyScheme());
                c.OperationFilter<SwaggerOperationFilter>();
                c.DescribeAllParametersInCamelCase();
            });
            


            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<IPrincipal>(sp => sp.GetService<IHttpContextAccessor>().HttpContext.User);


            services.Configure<TokenAuthenticationOptions>(Configuration.GetSection("TokenAuthentication"));
            services.Configure<ApplicationSettings>(Configuration.GetSection("ApplicationSettings"));

            services.AddHangfire(x =>
            {
#if DEBUG
                x.UseMemoryStorage(new MemoryStorageOptions());
#else
                x.UseSQLiteStorage("Data Source=Ombi.db;");
#endif
                x.UseActivator(new IoCJobActivator(services.BuildServiceProvider()));
            });

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            //loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            //loggerFactory.AddDebug();

            loggerFactory.AddSerilog();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            
            app.UseHangfireServer();
            app.UseHangfireDashboard();
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
                c.ShowJsonEditor();
            });


            // Setup the scheduler
            var jobSetup = (IJobSetup)app.ApplicationServices.GetService(typeof(IJobSetup));
            jobSetup.Setup();

            ConfigureAuth(app, (IOptions<TokenAuthenticationOptions>)app.ApplicationServices.GetService(typeof(IOptions<TokenAuthenticationOptions>)));

            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".map"] = "application/octet-stream";

            app.UseStaticFiles(new StaticFileOptions()
            {
                ContentTypeProvider = provider
            });

            app.UseMvc(routes =>
            { 
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");

                routes.MapSpaFallbackRoute(
                    name: "spa-fallback",
                    defaults: new { controller = "Home", action = "Index" });
            });
        }
    }
}
