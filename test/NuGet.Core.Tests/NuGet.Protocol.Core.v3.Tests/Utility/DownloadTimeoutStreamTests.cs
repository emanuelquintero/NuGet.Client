// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Diagnostics;

namespace NuGet.Protocol.Core.v3.Tests
{
    public class DownloadTimeoutStreamTests
    {
        [Fact]
        public void DownloadTimeoutStream_RejectsNullDownloadName()
        {
            // Arrange & Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new DownloadTimeoutStream(null, new MemoryStream(), TimeSpan.Zero));
            Assert.Equal("downloadName", exception.ParamName);
        }
        
        [Fact]
        public void DownloadTimeoutStream_RejectsNullStream()
        {
            // Arrange & Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new DownloadTimeoutStream("downloadName", null, TimeSpan.Zero));
            Assert.Equal("networkStream", exception.ParamName);
        }
        
        [Fact]
        public async Task DownloadTimeoutStream_SuccessAsync()
        {
            await VerifySuccessfulReadAsync(ReadStreamAsync);
        }
        
        [Fact]
        public async Task DownloadTimeoutStream_TimeoutAsync()
        {
            await VerifyTimeoutOnReadAsync(ReadStreamAsync);
        }
        
        [Fact]
        public async Task DownloadTimeoutStream_FailureAsync()
        {
            await VerifyFailureOnReadAsync(ReadStreamAsync);
        }

#if !NETSTANDARDAPP1_5
        [Fact]
        public async Task DownloadTimeoutStream_ApmNotSupported()
        {
            // Arrange
            var memoryStream = GetStream("foobar");
            var timeoutStream = new DownloadTimeoutStream("download", memoryStream, TimeSpan.FromSeconds(1));
            
            // Act & Assert
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                ReadStreamApm(timeoutStream));
        }
#endif
        
        [Fact]
        public async Task DownloadTimeoutStream_SuccessSync()
        {
            await VerifySuccessfulReadAsync(stream => Task.FromResult(ReadStream(stream)));
        }
        
        [Fact]
        public async Task DownloadTimeoutStream_TimeoutSync()
        {
            await VerifyTimeoutOnReadAsync(stream => Task.FromResult(ReadStream(stream)));
        }
        
        [Fact]
        public async Task DownloadTimeoutStream_FailureSync()
        {
            await VerifyFailureOnReadAsync(stream => Task.FromResult(ReadStream(stream)));
        }
        
        public async Task VerifyFailureOnReadAsync(Func<Stream, Task<string>> readAsync)
        {
            // Arrange
            var expected = new IOException();
            var memoryStream = GetStream("foobar");
            var slowStream = new SlowStream(memoryStream)
            {
                OnRead = (buffer, offset, count) => { throw expected; } 
            };
            var timeoutStream = new DownloadTimeoutStream("download", slowStream, TimeSpan.FromSeconds(1));
            
            // Act & Assert
            var actual = await Assert.ThrowsAsync<IOException>(() =>
                readAsync(timeoutStream));
            Assert.Same(expected, actual);
        }
        
        public async Task VerifyTimeoutOnReadAsync(Func<Stream, Task<string>> readAsync)
        {
            // Arrange
            var expectedDownload = "download";
            var expectedMilliseconds = 25;
            var memoryStream = GetStream("foobar");
            var slowStream = new SlowStream(memoryStream)
            {
                DelayPerByte = TimeSpan.FromMilliseconds(expectedMilliseconds * 2)
            };
            var timeoutStream = new DownloadTimeoutStream(
                expectedDownload,
                slowStream,
                TimeSpan.FromMilliseconds(expectedMilliseconds));
            
            // Act & Assert
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                readAsync(timeoutStream));
            Assert.Equal(
                $"The download of '{expectedDownload}' timed out because " +
                $"no data was received for {expectedMilliseconds}ms.",
                exception.Message);
            Assert.IsType<TimeoutException>(exception.InnerException);
        }
        
        private async Task VerifySuccessfulReadAsync(Func<Stream, Task<string>> readAsync)
        {
            // Arrange
            var expected = "foobar";
            var memoryStream = GetStream(expected);
            var timeoutStream = new DownloadTimeoutStream(
                "download",
                memoryStream,
                TimeSpan.FromMilliseconds(100));
            
            // Act
            var actual = await readAsync(timeoutStream);
            
            // Assert
            Assert.Equal(expected, actual);
        }
        
        private MemoryStream GetStream(string content)
        {
            return new MemoryStream(Encoding.ASCII.GetBytes(content));
        }
        
        private string ReadStream(Stream stream)
        {
            var destination = new MemoryStream();
            stream.CopyTo(destination, 1);
            return Encoding.ASCII.GetString(destination.ToArray());
        }

#if !NETSTANDARDAPP1_5
        private async Task<string> ReadStreamApm(Stream stream)
        {
            var destination = new MemoryStream();
            var buffer = new byte[1];
            var read = -1;
            while (read != 0)
            {
                read = await Task.Factory.FromAsync<byte[], int, int, int>(
                    stream.BeginRead,
                    stream.EndRead,
                    buffer,
                    0,
                    buffer.Length,
                    null);
                Console.WriteLine(read);
                destination.Write(buffer, 0, read);
            }
            
            return Encoding.ASCII.GetString(destination.ToArray());
        }
#endif

        private async Task<string> ReadStreamAsync(Stream stream)
        {
            var destination = new MemoryStream();
            await stream.CopyToAsync(destination, 1, CancellationToken.None);
            return Encoding.ASCII.GetString(destination.ToArray());
        }
    }
}
