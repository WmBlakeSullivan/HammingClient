using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace HammingTFTP
{
    class Program
    {
        static TimeSpan timeout = TimeSpan.FromSeconds(5);
        static byte[] NER = new byte[2] { 0x00, 0x01 };
        static byte[] ER = new byte[2] { 0x00, 0x02 };
        static byte[] DATA = new byte[2] { 0x00, 0x03 };
        static byte[] ACK = new byte[2] { 0x00, 0x04 };
        static byte[] ERROR = new byte[2] { 0x00, 0x05 };
        static byte[] NACK = new byte[2] { 0x00, 0x06 };

        static ArrayList fileBytes = new ArrayList();

        static void Main(string[] args)
        {
            //test for correct number of arguments
            if (args.Length != 3)
            {
                Console.WriteLine("Usage: HammingTFTP.exe [error|noerror] tftp-host file");
                Console.ReadKey();
                Environment.Exit(1);
            }

            //test for valid opcode
            byte[] transferType = new byte[2] { 0x00, 0x00 };
            if (args[0].ToLower().Equals("noerror"))
            {
                transferType = NER;
            }
            else if (args[0].ToLower().Equals("error"))
            {
                transferType = ER;
            }
            else
            {
                Console.WriteLine("Usage: HammingTFTP.exe [error|noerror] "
                    + "tftp-host file");
                Console.ReadKey();
                Environment.Exit(1);
            }

            //set servername for UDP
            String server = args[1];

            //set fileName for request and file writing
            String fileName = args[2];

            //setup initial send message
            byte[] fileNameBA = Encoding.ASCII.GetBytes(fileName);
            byte[] zeroByte = new byte[1] { 0x00 };
            byte[] transferMode = Encoding.ASCII.GetBytes("octet");

            //building fileRequest
            byte[][] byteArrays = new byte[][] { transferType, fileNameBA, zeroByte,
                transferMode, zeroByte };
            int messageLength = 0;
            foreach (byte[] ba in byteArrays)
            {
                messageLength += ba.Length;
            }
            byte[] fileRequest = new byte[messageLength];
            int offset = 0;
            foreach (byte[] ba in byteArrays)
            {
                Buffer.BlockCopy(ba, 0, fileRequest, offset, ba.Length);
                offset += ba.Length;
            }

            //starting block number
            byte[] blockNumber = new byte[2] { 0x00, 0x01 };

            UdpClient client = new UdpClient(server, 7000);

            client.Send(fileRequest, fileRequest.Length);
            IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

            int consecTimeouts = 0;
            IAsyncResult asyncResult;
            byte[] response;
            byte[] message;

            bool run = true;
            try
            {
                while (run)
                {
                    asyncResult = client.BeginReceive(null, null);
                    asyncResult.AsyncWaitHandle.WaitOne(timeout);
                    if (asyncResult.IsCompleted)
                    {
                        consecTimeouts = 0;

                        response = client.EndReceive(asyncResult, ref RemoteIpEndPoint);

                        byte[] opcode = new byte[2] { response[0], response[1] };
                        if(opcode[0] != 0x00)
                        {
                            message = new byte[4] { NACK[0], NACK[1], blockNumber[0], blockNumber[1] };
                            client.Send(message, message.Length);
                        }
                        else
                        {
                            switch (opcode[1])
                            {
                                case 0x03:
                                    if(response[2] == blockNumber[0] && response[3] == blockNumber[1])
                                    {
                                        byte[] data = new byte[response.Length - 4];
                                        Buffer.BlockCopy(response, 4, data, 0, response.Length - 4);
                                        if (VerifyData(data))
                                        {
                                            if (response.Length < 516 || response[response.Length-1] == 0x00)
                                                run = false;
                                            message = new byte[4] { ACK[0], ACK[1], blockNumber[0], blockNumber[1] };
                                            client.Send(message, message.Length);
                                            blockNumber = Increment(blockNumber);
                                        }
                                        else
                                        {
                                            message = new byte[4] { NACK[0], NACK[1], blockNumber[0], blockNumber[1] };
                                            client.Send(message, message.Length);
                                        }
                                    }
                                    else
                                    {
                                        message = new byte[4] { NACK[0], NACK[1], blockNumber[0], blockNumber[1] };
                                        client.Send(message, message.Length);
                                    }
                                    break;
                                case 0x05:
                                    byte[] errorCode = new byte[2] { response[2], response[3] };
                                    byte[] errorMessage = new byte[response.Length - 5];
                                    for(int i = 4; i < response.Length - 1; i++)
                                    {
                                        errorMessage[i - 4] = response[i];
                                    }
                                    Console.WriteLine(Encoding.ASCII.GetString(errorCode)+": "+Encoding.ASCII.GetString(errorMessage));
                                    Environment.Exit(1);
                                    break;
                                default:
                                    message = new byte[4] { NACK[0], NACK[1], blockNumber[0], blockNumber[1] };
                                    client.Send(message, message.Length);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        consecTimeouts++;
                        if(consecTimeouts >= 6)
                        {
                            Console.WriteLine("Connection to the server has timed out. Exiting program.");
                            Console.ReadKey();
                            Environment.Exit(1);
                        }
                        else
                        {
                            message = new byte[4] { NACK[0], NACK[1], blockNumber[0], blockNumber[1] };
                            client.Send(message, message.Length);
                        }
                    }
                }
                client.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine("The program has encountered a fatal error during file read. Exiting.");
                Console.ReadKey();
                Environment.Exit(2);
            }
            try
            {
                BinaryWriter bw = new BinaryWriter(File.Create(fileName));
                foreach( byte b in fileBytes)
                {
                    bw.Write(b);
                    bw.Flush();
                }
                bw.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine("The program has encountered a fatal error during file write. Exiting.");
                Console.ReadKey();
                Environment.Exit(3);
            }
            Console.WriteLine("Transfer Complete.");
            Console.ReadKey();
        }

        private static bool VerifyData(byte[] data)
        {
            bool goodData = true;
            bool[] leftoverBits = new bool[0];
            for(int i = 0; i <= data.Length / 16 && goodData; i++)
            {
                for (int j = 0; j < 4 && (i*4+j) < data.Length && goodData; j++)
                {
                    byte[] chunkBytes = new byte[4];
                    for(int k = 3; k >= 0 && ((i*4+j)*4+k) < data.Length; k--)
                    {
                        Buffer.BlockCopy(data, ((i * 4 + j) * 4 + k), chunkBytes, 3 - k, 1);
                    }
                    foreach(byte b in chunkBytes)
                    {
                        Console.Write(Convert.ToString(b, 2).PadLeft(8, '0') + "\t");
                    }
                    Console.WriteLine();
                    BitArray chunk = new BitArray(chunkBytes);
                    for (int k = 0; k < chunkBytes.Length; k++)
                    {
                        for (int l = 0; l < 4; l++)
                        {
                            bool temp = chunk.Get(k * 8 + l);
                            chunk.Set(k * 8 + l, chunk.Get(k * 8 + (7 - l)));
                            chunk.Set(k * 8 + (7 - l), temp);
                        }
                        for (int l = 0; l < 8; l++)
                        {
                            char bit = (chunk.Get(k * 8 + l)) ? '1' : '0';
                            Console.Write(bit);
                        }
                        Console.Write("\t");
                    }
                    Console.WriteLine();
                    int bitToFlip = 0;
                    for(double k = 0; k < 5; k++)
                    {
                        int parityBit = (int)Math.Pow(2, k);
                        bitToFlip += verifyHammingParity(chunk, parityBit) ? 0 : parityBit;
                    }
                    if (bitToFlip != 0)
                    {
                        chunk.Set(chunkBytes.Length * 8 - bitToFlip, !chunk.Get(chunkBytes.Length * 8 - bitToFlip));
                    }
                    int overallParity = 0;
                    for(int k = 0; k < chunk.Length; k++)
                    {
                        overallParity += (chunk.Get(k)) ? 1 : 0;
                    }
                    if (overallParity % 2 != 0)
                    {
                        goodData = false;
                        break;
                    }
                    BitArray flipBits = new BitArray(leftoverBits.Length + 26);
                    for(int k = 0; k < leftoverBits.Length; k++)
                    {
                        flipBits.Set(k, leftoverBits[leftoverBits.Length - 1 - k]);
                    }
                    for (int k = 0, l = 30; k < 26; k++, l--)
                    {
                        if (l == 15 || l == 7 || l == 3)
                            l--;
                        flipBits.Set(k + leftoverBits.Length, chunk.Get(31-l));
                    }
                    int byteCounter = -1;
                    for (int k = 0; k < flipBits.Length; k++)
                    {
                        char bit = flipBits.Get(k) ? '1' : '0';
                        Console.Write(bit);
                        byteCounter = (byteCounter + 1) % 8;
                        if(byteCounter == 7)
                            Console.Write("\t");
                    }
                    Console.WriteLine();
                    int flipIterations = flipBits.Length / 8;
                    if (flipBits.Length % 8 != 0) flipIterations++;
                    for (int k = 0; k < flipIterations; k++)
                    {
                        int swaps;
                        if(flipBits.Length - (k*8 + 8) >= 0)
                        {
                            swaps = 4;
                        }
                        else
                        {
                            swaps = (flipBits.Length - (k * 8)) / 2;
                        }
                        for (int l = 0; l < swaps; l++)
                        {
                            bool temp = flipBits.Get(k * 8 + l);
                            flipBits.Set(k * 8 + l, flipBits.Get(k * 8 + (2*swaps - 1 - l)));
                            flipBits.Set(k * 8 + (2*swaps - 1 - l), temp);
                        }
                    }
                    byteCounter = -1;
                    for (int k = 0; k < flipBits.Length; k++)
                    {
                        char bit = flipBits.Get(k) ? '1' : '0';
                        Console.Write(bit);
                        byteCounter = (byteCounter + 1) % 8;
                        if (byteCounter == 7)
                            Console.Write("\t");
                    }
                    Console.WriteLine();
                    int byteIterations = flipBits.Length / 8;
                    for (int k = 0; k < byteIterations; k++)
                    {
                        BitArray buildByte = new BitArray(8);
                        for(int l = 0; l < 8; l++)
                        {
                            buildByte.Set(l, flipBits.Get((k * 8) + l));
                        }
                        byte fileByte = ConvertToByte(buildByte);
                        Console.Write(Convert.ToString(fileByte, 2).PadLeft(8, '0') + "\t");
                        fileBytes.Add(fileByte);
                    }
                    int leftovers = flipBits.Length % 8;
                    leftoverBits = new bool[leftovers];
                    for(int k = 0; k < leftovers; k++)
                    {
                        leftoverBits[k] = flipBits.Get(byteIterations * 8 + k);
                        char bit = leftoverBits[k] ? '1' : '0';
                        Console.Write(bit);
                    }
                    Console.WriteLine("\n");
                }
            }

            return goodData;
        }

        private static bool verifyHammingParity(BitArray chunk, int parityBit)
        {

            int parityCounter = 0;
            for(int i = 1; i < 32/parityBit; i+=2)
            {
                for(int j = 0; j < parityBit; j++)
                {
                    if (chunk.Get(31 - ((i * parityBit) + j - 1)))
                    {
                        parityCounter++;
                    }
                }
            }

            return parityCounter % 2 == 0;
        }

        private static byte ConvertToByte(BitArray bits)
        {
            BitArray bitsFlipped = new BitArray(bits);
            for (int i = 0; i < 4; i++)
            {
                bool temp = bitsFlipped.Get(i);
                bitsFlipped.Set(i, bitsFlipped.Get(7-i));
                bitsFlipped.Set(7-i, temp);
            }
            byte[] bytes = new byte[1];
            bitsFlipped.CopyTo(bytes, 0);
            return bytes[0];

        }

        private static byte[] Increment(byte[] ba)
        {
            byte[] rv = ba;
            int index = rv.Length - 1;
            while (index >= 0)
            {
                if (rv[index] < 255)
                {
                    rv[index]++;
                    break;
                }
                else
                {
                    rv[index--] = 0;
                }
            }
            return rv;
        }
    }
}
