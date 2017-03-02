// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Testing;

namespace Microsoft.AspNetCore.Server.Kestrel.Performance
{
    [Config(typeof(CoreConfig))]
    public class RequestHeadersParsing
    {
        private const int InnerLoopCount = 512;

        private const string singleRequestHeader =
            "Host: www.example.com\r\n";

        private static string[] liveaspnetRequestHeaders = new[]
        {
            "Host: live.asp.net\r\n",
            "Connection: keep-alive\r\n",
            "Upgrade-Insecure-Requests: 1\r\n",
            "User-Agent: Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/54.0.2840.99 Safari/537.36\r\n",
            "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8\r\n",
            "DNT: 1\r\n",
            "Accept-Encoding: gzip, deflate, sdch, br\r\n",
            "Accept-Language: en-US,en;q=0.8\r\n",
            "Cookie: __unam=7a67379-1s65dc575c4-6d778abe-1; omniID=9519gfde_3347_4762_8762_df51458c8ec2\r\n",
            "\r\n"
        };

        private static readonly byte[] _singleRequestHeader =
            Encoding.ASCII.GetBytes(singleRequestHeader + "\r\n");
        private static readonly byte[][] _singleRequestHeaderMultipleCalls = new[]
        {
            Encoding.ASCII.GetBytes(singleRequestHeader),
            Encoding.ASCII.GetBytes("\r\n"),
        };

        private static readonly byte[] _liveaspnetRequestHeaders =
            Encoding.ASCII.GetBytes(string.Join(string.Empty, liveaspnetRequestHeaders));
        private static readonly byte[][] _liveaspnetRequestHeadersMultipleCalls =
            liveaspnetRequestHeaders
                .Select(header => Encoding.ASCII.GetBytes(header))
                .ToArray();

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
        public void ParseSingleRequestHeader()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_singleRequestHeader);
                ParseData(checkResult: true);
            }
        }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParseSingleRequestHeaderMultipleCalls()
        {
            for (var i = 0; i < InnerLoopCount; i++)
             {
                for (var j = 0; j < _singleRequestHeaderMultipleCalls.Length; j++)
                {
                    Pipe.InsertData(_singleRequestHeaderMultipleCalls[j]);
                    ParseData(checkResult: j == _singleRequestHeaderMultipleCalls.Length - 1);
                }
             }
         }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParseLiveAspNet()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                Pipe.InsertData(_liveaspnetRequestHeaders);
                ParseData(checkResult: true);
            }
        }

        [Benchmark(OperationsPerInvoke = InnerLoopCount)]
        public void ParseLiveAspNetMultipleCalls()
        {
            for (var i = 0; i < InnerLoopCount; i++)
            {
                for (var j = 0; j < _liveaspnetRequestHeadersMultipleCalls.Length; j++)
                {
                    Pipe.InsertData(_liveaspnetRequestHeadersMultipleCalls[j]);
                    ParseData(checkResult: j == _liveaspnetRequestHeadersMultipleCalls.Length - 1);
                }
            }
        }

        private void ParseData(bool checkResult)
        {
            do
            {
                var awaitable = Pipe.Reader.ReadAsync();
                if (!awaitable.IsCompleted)
                {
                    // No more data
                    return;
                }

                Frame.Reset();

                var result = Pipe.Reader.ReadAsync().GetAwaiter().GetResult();
                var readableBuffer = result.Buffer;

                Frame.InitializeHeaders();

                ReadCursor consumed;
                ReadCursor examined;
                if (!Frame.TakeMessageHeaders(readableBuffer, (FrameRequestHeaders)Frame.RequestHeaders, out consumed, out examined) && checkResult)
                {
                    RequestParsing.ThrowInvalidRequestHeaders();
                }
                Pipe.Reader.Advance(consumed, examined);
            }
            while(true);
        }
    }
}
