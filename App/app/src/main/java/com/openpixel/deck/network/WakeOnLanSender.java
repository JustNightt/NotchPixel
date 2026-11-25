package com.openpixel.deck.network;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;

/**
 * Sends a standard 102-byte Wake-on-LAN UDP magic packet to wake a paired Windows PC.
 */
public final class WakeOnLanSender {
    private WakeOnLanSender() {}

    public static boolean sendMagicPacket(String macAddress) {
        if (macAddress == null || macAddress.trim().isEmpty()) return false;
        String clean = macAddress.replace(":", "").replace("-", "").trim();
        if (clean.length() != 12) return false;

        try {
            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++) {
                macBytes[i] = (byte) Integer.parseInt(clean.substring(i * 2, i * 2 + 2), 16);
            }

            byte[] packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = (byte) 0xFF;
            for (int i = 6; i < packet.length; i += 6) {
                System.arraycopy(macBytes, 0, packet, i, 6);
            }

            try (DatagramSocket socket = new DatagramSocket()) {
                socket.setBroadcast(true);
                InetAddress bcast = InetAddress.getByName("255.255.255.255");
                socket.send(new DatagramPacket(packet, packet.length, bcast, 9));
                socket.send(new DatagramPacket(packet, packet.length, bcast, 7));
            }
            return true;
        } catch (Exception ignored) {
            return false;
        }
    }
}
