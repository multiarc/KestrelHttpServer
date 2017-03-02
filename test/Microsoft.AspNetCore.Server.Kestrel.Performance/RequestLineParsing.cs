// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Testing;

namespace Microsoft.AspNetCore.Server.Kestrel.Performance
{
    [Config(typeof(CoreConfig))]
    public class RequestLineParsing
    {
        private const int InnerLoopCount = 512;

        private const string plaintextRequestLine =
            "GET /plaintext HTTP/1.1\r\n";

        private const string queryStringRequestLine =
            "GET /plaintext?arg=val HTTP/1.1\r\n";

        private const string percentEncodedRequestLine =
            "GET /encoded%20plaintext HTTP/1.1\r\n";

        private const string percentEncodedQueryStringRequestLine =
            "GET /encoded%20plaintext?arg=val HTTP/1.1\r\n";

        private static readonly byte[] _plaintextRequestLine = Encoding.ASCII.GetBytes(plaintextRequestLine);

        private static readonly byte[] _queryStringRequestLine = Encoding.ASCII.GetBytes(queryStringRequestLine);
        private static readonly byte[] _percentEncodedRequestLine = Encoding.ASCII.GetBytes(percentEncodedRequestLine);
        private static readonly byte[] _percentEncodedQueryStringRequestLine = Encoding.ASCII.GetBytes(percentEncodedQueryStringRequestLine);

        public IPipe Pipe { get; set; }

        public Frame<object> Frame { get; set; }

        public PipeFactory PipelineFactory { get; set; }

        [Setup]
        public void Setup()
        {
            var connectionContext = new MockConnection(new KestrelServerOptions());
            Frame = new Frame<object>(application: null, context: connectionContext);
            PipelineFactory = new PipeFactory();
            Pipe = PipelineFactory.Create();
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = InnerLoopCount)]
        public void ParsePlaintextRequestLine()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_plaintextRequestLine);
                ParseData();
            }
        }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParseQueryStringRequestLine()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_queryStringRequestLine);
                ParseData();
            }
        }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParsePercentEncodedRequestLine()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_percentEncodedRequestLine);
                ParseData();
            }
        }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParsePercentEncodedQueryStringRequestLine()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_percentEncodedQueryStringRequestLine);
                ParseData();
            }
        }

        private void ParseData()
        {
            do
            {
                var awaitable = Pipe.Reader.ReadAsync();
                if (!awaitable.IsCompleted)
                {
                    // No more data
                    return;
                }

                var result = awaitable.GetAwaiter().GetResult();
                var readableBuffer = result.Buffer;

                Frame.Reset();

                ReadCursor consumed;
                ReadCursor examined;
                if (!Frame.TakeStartLine(readableBuffer, out consumed, out examined))
                {
                    RequestParsing.ThrowInvalidRequestLine();
                }
                Pipe.Reader.Advance(consumed, examined);
            }
            while(true);
        }
    }
}
