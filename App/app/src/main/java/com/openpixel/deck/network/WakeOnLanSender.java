package com.openpixel.deck.network;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.Enumeration;
import java.util.LinkedHashSet;
import java.util.Set;

/**
 * Sends a standard 102-byte Wake-on-LAN UDP magic packet to wake a paired Windows PC.
 */
public final class WakeOnLanSender {
    private WakeOnLanSender() {}

    public static byte[] buildMagicPacket(String macAddress) {
        if (macAddress == null || macAddress.trim().isEmpty()) {
            throw new IllegalArgumentException("MAC address is empty");
        }
        String clean = macAddress.replace(":", "").replace("-", "").trim();
        if (clean.length() != 12) {
            throw new IllegalArgumentException("MAC address must be 12 hex digits");
        }
        byte[] macBytes = new byte[6];
        for (int i = 0; i < 6; i++) {
            int val = Integer.parseInt(clean.substring(i * 2, i * 2 + 2), 16);
            if (val < 0 || val > 255) {
                throw new IllegalArgumentException("Invalid MAC byte");
            }
            macBytes[i] = (byte) val;
        }
        byte[] packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = (byte) 0xFF;
        for (int i = 6; i < packet.length; i += 6) {
            System.arraycopy(macBytes, 0, packet, i, 6);
        }
        return packet;
    }

    public static boolean sendMagicPacket(String macAddress) {
        try {
            byte[] packet = buildMagicPacket(macAddress);
            Set<InetAddress> targets = new LinkedHashSet<>();
            targets.add(InetAddress.getByName("255.255.255.255"));

            try {
                Enumeration<NetworkInterface> ifaces = NetworkInterface.getNetworkInterfaces();
                if (ifaces != null) {
                    while (ifaces.hasMoreElements()) {
                        NetworkInterface ni = ifaces.nextElement();
                        if (!ni.isUp() || ni.isLoopback()) continue;
                        for (InterfaceAddress ia : ni.getInterfaceAddresses()) {
                            InetAddress bcast = ia.getBroadcast();
                            if (bcast != null) {
                                targets.add(bcast);
                            }
                        }
                    }
                }
            } catch (Exception ignored) { }

            try (DatagramSocket socket = new DatagramSocket()) {
                socket.setBroadcast(true);
                for (InetAddress target : targets) {
                    try {
                        socket.send(new DatagramPacket(packet, packet.length, target, 9));
                        socket.send(new DatagramPacket(packet, packet.length, target, 7));
                    } catch (Exception ignored) { }
                }
            }
            return true;
        } catch (Exception ignored) {
            return false;
        }
    }
}
