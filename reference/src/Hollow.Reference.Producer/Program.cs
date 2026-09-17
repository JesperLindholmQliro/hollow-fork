/*
 *  Copyright 2016 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

using Hollow.Reference.Infrastructure;
using Hollow.Reference.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The producer half of the reference implementation. It publishes a catalogue every ten seconds, for
// as long as it is left running, into whichever infrastructure appsettings.json names.
//
// Ported from how.hollow.producer.Producer. What was a main method wiring up a filesystem publisher by
// hand is a host here: configuration decides the infrastructure, the cycle runs as a hosted service,
// and Ctrl-C stops it between cycles rather than in the middle of one.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,

    // The settings file is linked into the build output rather than sitting in the project directory,
    // so the content root has to be where the binary is. Without this, `dotnet run` would look in the
    // project directory and find nothing.
    ContentRootPath = AppContext.BaseDirectory,
});

// A publisher, an announcer, a blob retriever and an announcement watcher, chosen by Hollow:Mode. The
// producer needs the first two to publish and the second two to restore.
builder.Services.AddHollowReferenceInfrastructure(builder.Configuration);

builder.Services.AddHostedService<PublishingService>();

await builder.Build().RunAsync().ConfigureAwait(false);
