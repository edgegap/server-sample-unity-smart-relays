using System;
using System.Text;
using LightNet;
using UnityEngine;

namespace LightNet.Edgegap.Sample
{
    // By LightNet convention the first byte of every message is its type ID.

    /// <summary>Host -> new client: your player id. Reliable.</summary>
    class WelcomeData : NetworkData
    {
        public const byte ID = 10;
        public byte PlayerId;

        public byte[] Serialize()
        {
            return new byte[] { ID, PlayerId };
        }

        public void Deserialize(byte[] data)
        {
            PlayerId = data[1];
        }
    }

    /// <summary>Everyone: where a player's cube is. Unreliable, sent every frame. The host forwards client states to the other clients.</summary>
    class PlayerStateData : NetworkData
    {
        public const byte ID = 11;
        public byte PlayerId;
        public Vector3 Position;
        public Quaternion Rotation;

        const int SIZE = sizeof(byte) * 2 + sizeof(float) * 7;
        readonly byte[] Data = new byte[SIZE];

        public byte[] Serialize()
        {
            int offset = 0;
            Data[offset++] = ID;
            Data[offset++] = PlayerId;
            SerializationHelper.ToBytes(in Position, Data, ref offset);
            SerializationHelper.ToBytes(in Rotation, Data, ref offset);
            return Data;
        }

        public void Deserialize(byte[] data)
        {
            int offset = 1;
            PlayerId = data[offset++];
            SerializationHelper.FromBytes(data, ref offset, out Position);
            SerializationHelper.FromBytes(data, ref offset, out Rotation);
        }
    }

    /// <summary>Host -> clients: a player left. Reliable.</summary>
    class PlayerLeftData : NetworkData
    {
        public const byte ID = 12;
        public byte PlayerId;

        public byte[] Serialize()
        {
            return new byte[] { ID, PlayerId };
        }

        public void Deserialize(byte[] data)
        {
            PlayerId = data[1];
        }
    }

    /// <summary>Chat line. Reliable. Clients send to the host, the host forwards to everyone.</summary>
    class ChatData : NetworkData
    {
        public const byte ID = 13;
        public byte PlayerId;
        public string Text = "";

        public byte[] Serialize()
        {
            byte[] text = Encoding.UTF8.GetBytes(Text ?? "");
            byte[] data = new byte[2 + text.Length];
            data[0] = ID;
            data[1] = PlayerId;
            Array.Copy(text, 0, data, 2, text.Length);
            return data;
        }

        public void Deserialize(byte[] data)
        {
            PlayerId = data[1];
            Text = Encoding.UTF8.GetString(data, 2, data.Length - 2);
        }
    }

    /// <summary>
    /// Automated test message (-e2e): the client sends numbered messages of various sizes on the
    /// reliable channel, the host echoes them back; the client checks order, content and round trip.
    /// </summary>
    class EchoTestData : NetworkData
    {
        public const byte ID = 14;
        public uint Sequence;
        public long SentTicks;
        public byte[] Payload = new byte[0];

        public byte[] Serialize()
        {
            byte[] data = new byte[1 + sizeof(uint) + sizeof(long) + Payload.Length];
            data[0] = ID;
            Array.Copy(BitConverter.GetBytes(Sequence), 0, data, 1, sizeof(uint));
            Array.Copy(BitConverter.GetBytes(SentTicks), 0, data, 1 + sizeof(uint), sizeof(long));
            Array.Copy(Payload, 0, data, 1 + sizeof(uint) + sizeof(long), Payload.Length);
            return data;
        }

        public void Deserialize(byte[] data)
        {
            Sequence = BitConverter.ToUInt32(data, 1);
            SentTicks = BitConverter.ToInt64(data, 1 + sizeof(uint));
            int header = 1 + sizeof(uint) + sizeof(long);
            Payload = new byte[data.Length - header];
            Array.Copy(data, header, Payload, 0, Payload.Length);
        }

        public static byte[] MakePayload(uint sequence, int size)
        {
            byte[] payload = new byte[size];
            for (int i = 0; i < size; ++i)
            {
                payload[i] = (byte)(sequence * 31 + i);
            }
            return payload;
        }
    }
}
