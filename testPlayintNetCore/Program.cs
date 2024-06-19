using NAudio;
using NAudio.Wave;

using System;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;



using testPlayintNetCore;

namespace testPlayintNetCore
{
    internal class Program
    {
        static ManualResetEventSlim pauseEvent = new ManualResetEventSlim(true);

        static void Main(string[] args)
        {
            string fileName = ".\\ST_Life_augmented.mp3";
            byte[] buffer = ReadAndConvertAudioFile(fileName);

            //SendToIOCard(buffer);
            //PlayAudioData(buffer, 8000, 16, 2);

            Task playbackTask = Task.Run(() => PlayAudioDataWS(buffer, 8000, 16, 2, "wss://localhost:5005"));

            Console.WriteLine("Playing, click to pause");
            /*Console.ReadLine();
            Pause();
            Console.WriteLine("Paused, click to resume");
            Console.ReadLine();
            Resume();
            Console.WriteLine("Resumed...");*/

            Task.WaitAll(playbackTask);
        }

        static void Pause()
        {
            pauseEvent.Reset();
        }

        static void Resume()
        {
            pauseEvent.Set();
        }

        static public void CheckPause()
        {
            //Console.WriteLine("waiting for pauseEvent");
            pauseEvent.Wait();
            //Console.WriteLine("pauseEvent happened");
        }

        static byte[] ReadAndConvertAudioFile(string filePath)
        {
            string server = "192.168.2.124"; // The IP address of the server
            int port = 49151;             // The port to send data to

            using (var audioReader = CreateAudioFileReader(filePath))
            {
                // Define the desired output format: 8000 Hz, 16-bit, mono or stereo based on input
                var outFormat = new WaveFormat(8000, 16, 2);

                // Resample the audio data
                using (var resampler = new MediaFoundationResampler(audioReader, outFormat))
                {
                    resampler.ResamplerQuality = 60; // Set resampling quality

                    using (var memoryStream = new MemoryStream())
                    {
                        var buffer = new byte[outFormat.AverageBytesPerSecond];
                        int bytesRead;

                        // Read the resampled audio data into memory stream
                        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            memoryStream.Write(buffer, 0, bytesRead);
                        }

                        // Get the raw PCM data
                        byte[] audioData = memoryStream.ToArray();

                        // Convert the data to big-endian format
                        return AdjustVolume(audioData, 0.5);  ConvertToBigEndian(audioData);
                    }
                }
            }
        }

        public static byte[] AdjustVolume(byte[] audioBytes, double gain)
        {
            // Ensure gain is positive
            if (gain < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be a positive value.");
            }

            // Create a new array for the adjusted audio bytes
            byte[] adjustedBytes = new byte[audioBytes.Length];

            // Iterate through each pair of bytes (16-bit samples)
            for (int i = 0; i < audioBytes.Length; i += 2)
            {
                // Convert little-endian bytes to a short
                short sample = BitConverter.ToInt16(audioBytes, i);

                // Adjust the volume of the sample
                double adjustedSample = sample * gain;

                // Clamp the adjusted sample to the range of short to prevent overflow
                if (adjustedSample > short.MaxValue)
                {
                    adjustedSample = short.MaxValue;
                }
                else if (adjustedSample < short.MinValue)
                {
                    adjustedSample = short.MinValue;
                }

                // Convert the adjusted sample back to bytes
                byte[] adjustedSampleBytes = BitConverter.GetBytes((short)adjustedSample);

                // Copy the adjusted sample bytes to the adjustedBytes array
                adjustedBytes[i] = adjustedSampleBytes[0];
                adjustedBytes[i + 1] = adjustedSampleBytes[1];
            }

            return adjustedBytes;
        }

        /*private static byte[] AdjustVolume(byte[] audioData, double v)
        {
            byte[] data = new byte[audioData.Length];

            for (int i = 0; i < audioData.Length; i+=2)
            {
                int value = audioData[i+1] + 256 * audioData[i];
                value = (int)Math.Floor(value * v);
                data[i + 1] = (byte)(value % 256);
                data[i] = (byte)(value / 256);
            }
            return data;                
        }*/

        static WaveStream CreateAudioFileReader(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            switch (extension)
            {
                case ".wav":
                    return new WaveFileReader(filePath);
                case ".mp3":
                    return new Mp3FileReader(filePath);
                case ".aiff":
                    return new AiffFileReader(filePath);
                // Add more cases here for other supported formats
                default:
                    throw new InvalidOperationException("Unsupported audio format: " + extension);
            }
        }

        static byte[] ConvertToBigEndian(byte[] data)
        {
            // Convert 16-bit PCM little-endian to big-endian
            for (int i = 0; i < data.Length; i += 2)
            {
                byte temp = data[i];
                data[i] = data[i + 1];
                data[i + 1] = temp;
            }
            return data;
        }

        static void PlayAudioDataWS(byte[] audioData, int sampleRate, int bitDepth, int channels, string v)
        {
            //string certPath = "C:\\bariks\\Mosquitto\\certs\\barikshealth.com-2028\\barikshealth.com.pfx";

            X509Certificate2 clientCertificate = new X509Certificate2();

            HttpClientHandler httpClientHandler = new HttpClientHandler();
            httpClientHandler.ClientCertificates.Add(clientCertificate);
            httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            ClientWebSocket clientWebSocket = new ClientWebSocket();

            using (var memoryStream = new MemoryStream(audioData))            
            {
                using (HttpMessageHandler handler = new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true,
                        ClientCertificates = new X509CertificateCollection { clientCertificate }
                    }
                })
                {
                    clientWebSocket.ConnectAsync(new Uri("wss://box.barikshealth.com:8000/ws/send"), CancellationToken.None).Wait();
                    Console.WriteLine("Connected!");
                    var waveFormat = new WaveFormat(sampleRate, bitDepth, channels);
                    var chunkedWaveProvider = new ChunkedWaveProvider(audioData, waveFormat);
                    int offset = 0;

                    using (var waveOut = new WaveOutEvent())
                    {
                        byte[] buffer = new byte[1280];
                        DateTime start = DateTime.Now;
                        TimeSpan timeStamp = TimeSpan.Zero;
                        while (offset < audioData.Length)
                        {
                            chunkedWaveProvider.Read(buffer, 0, 1280);
                            offset += buffer.Length;
                            timeStamp += TimeSpan.FromMilliseconds(buffer.Length / 32);
                            Console.WriteLine($"offset: {offset}, Length = {audioData.Length}, timestamp = {timeStamp}, time now = {DateTime.Now}");

                            try
                            {
                                clientWebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
                            }
                            catch (Exception ex)
                            {
                                clientWebSocket.Abort();
                                clientWebSocket.Dispose();
                                clientWebSocket = new ClientWebSocket();
                                clientWebSocket.ConnectAsync(new Uri("wss://box.barikshealth.com:8000/ws/send"), CancellationToken.None).Wait();
                                clientWebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
                                Console.WriteLine($"failed on exception, ex = {ex}");
                            }

                            double milisecondsToSleep = Math.Max((offset / 32) - (DateTime.Now - start).TotalMilliseconds, 0);
                            TimeSpan timeToSleep = TimeSpan.FromMilliseconds(milisecondsToSleep);
                            //Console.WriteLine($"timeToSleep = {timeToSleep.TotalMilliseconds}");
                            
                            Thread.Sleep(timeToSleep);                            
                        }


                        /*waveOut.Init(chunkedWaveProvider);
                        CheckPause();
                        waveOut.Play();


                        while (waveOut.PlaybackState == PlaybackState.Playing)
                        {                        
                            System.Threading.Thread.Sleep(100);
                        }*/
                    }
                }                    
            }
            clientWebSocket.Dispose ();
        }

        static void PlayAudioData(byte[] audioData, int sampleRate, int bitDepth, int channels, string v)
        {
            // Convert big-endian data to little-endian
            byte[] littleEndianData = ConvertToLittleEndian(audioData);
            string server = "192.168.2.124"; // The IP address of the server
            int port = 49151;             // The port to send data to

            //using (ClientWebSocket clientWebSocket = new ClientWebSocket())
            using (var memoryStream = new MemoryStream(audioData))
            using (UdpClient client = new UdpClient())
            {
                //clientWebSocket.ConnectAsync(new Uri("ws://localhost:8000"), CancellationToken.None).Wait();
                //Console.WriteLine("Connected!");
                var waveFormat = new WaveFormat(sampleRate, bitDepth, channels);
                var chunkedWaveProvider = new ChunkedWaveProvider(audioData, waveFormat);
                int offset = 0;
                TimeSpan timeStamp = TimeSpan.Zero;

                using (var waveOut = new WaveOutEvent())
                {
                    byte[] buffer = new byte[1292];
                    DateTime start = DateTime.Now;
                    while(offset < audioData.Length)
                    {
                        chunkedWaveProvider.Read(buffer, 12, 1280);
                        offset += 1280;
                        timeStamp += TimeSpan.FromMilliseconds(1280 / 32);

                        timeStamp += TimeSpan.FromMilliseconds(buffer.Length / 32);
                        Console.WriteLine($"offset: {offset}, Length = {audioData.Length}, timestamp = {timeStamp}, time now = {DateTime.Now}");

                        //clientWebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
                        PutRtpHeader(buffer);
                        client.Send(buffer, 1292, server, port);
                        double PacketsIn = ((DateTime.Now - start).TotalMilliseconds + offset / 32) / (1292 / 32);
                        if(PacketsIn > 5)
                        {                            
                            double milisecondsToSleep = Math.Max((offset / 32) - (DateTime.Now - start).TotalMilliseconds - 5 * (1292 / 32), 0);
                            TimeSpan timeToSleep = TimeSpan.FromMilliseconds(milisecondsToSleep);
                            Thread.Sleep(timeToSleep);
                        }
                        

                        //Console.WriteLine($"timeToSleep = {timeToSleep.TotalMilliseconds}");

                        
                    }
                    
                    
                    /*waveOut.Init(chunkedWaveProvider);
                    CheckPause();
                    waveOut.Play();


                    while (waveOut.PlaybackState == PlaybackState.Playing)
                    {                        
                        System.Threading.Thread.Sleep(100);
                    }*/
                }
            }
        }

        private static void PutRtpHeader(byte[] buffer)
        {
            buffer[0] = 0x80; // Version: 2, Padding: 0, Extension: 0, CSRC Count: 0
            buffer[1] = 0x60; // Marker: 0, Payload Type: 96
            buffer[2] = 0x00; // Sequence Number (High Byte)
            buffer[3] = 0x01; // Sequence Number (Low Byte)
            buffer[4] = 0x00; // Timestamp (1st Byte)
            buffer[5] = 0x00; // Timestamp (2nd Byte)
            buffer[6] = 0x00; // Timestamp (3rd Byte)
            buffer[7] = 0x00; // Timestamp (4th Byte)
            buffer[8] = 0x12; // SSRC (1st Byte)
            buffer[9] = 0x34; // SSRC (2nd Byte)
            buffer[10] = 0x56; // SSRC (3rd Byte)
            buffer[11] = 0x78; // SSRC (4th Byte)
        }

        static byte[] ConvertToLittleEndian(byte[] data)
        {
            for (int i = 0; i < data.Length; i += 2)
            {
                byte temp = data[i];
                data[i] = data[i + 1];
                data[i + 1] = temp;
            }
            return data;
        }


        static void SendToIOCard(byte[] audioData)
        {
            // Implement the logic to send audioData to your IO card.
            // This will depend on the specific API or SDK provided by the IO card manufacturer.
            Console.WriteLine("Audio data ready to be sent to the IO card.");
        }
    }
}

public class ChunkedWaveProvider : IWaveProvider
{
    private readonly byte[] audioData;
    private int position;
    private readonly WaveFormat waveFormat;

    public ChunkedWaveProvider(byte[] audioData, WaveFormat waveFormat)
    {
        this.audioData = audioData;
        this.waveFormat = waveFormat;
        this.position = 0;
    }

    public WaveFormat WaveFormat => waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        CheckPause();

        int bytesToRead = Math.Min(count, audioData.Length - position);
        if (bytesToRead > 0)
        {
            Array.Copy(audioData, position, buffer, offset, bytesToRead);
            position += bytesToRead;
        }
        return bytesToRead;
    }

    public void CheckPause()
    {
        Program.CheckPause();
    }
}