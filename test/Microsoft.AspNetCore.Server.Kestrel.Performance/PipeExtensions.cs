// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO.Pipelines;

namespace Microsoft.AspNetCore.Server.Kestrel.Performance
{
    public static class PipeExtensions
    {
        public static void InsertData(this IPipe pipe, byte[] bytes)
        {
            // There should not be any backpressure and task completes immediately
            pipe.Writer.WriteAsync(bytes).GetAwaiter().GetResult();
        }
    }
}
