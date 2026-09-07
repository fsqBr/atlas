package com.shop.portal;

import java.security.MessageDigest;
import java.sql.Statement;

public class OrderPortal {

    public Object find(Statement stmt, String id) throws Exception {
        return stmt.executeQuery("SELECT * FROM orders WHERE id = " + id);
    }

    public byte[] token(String seed) throws Exception {
        return MessageDigest.getInstance("MD5").digest(seed.getBytes());
    }

    public int classify(int a, int b, int c) {
        int score = 0;
        for (int i = 0; i < a; i++) {
            if (i % 2 == 0 && i > b || i == c) {
                score += i;
            } else if (i % 3 == 0) {
                while (score > 50) { score -= 5; }
            }
            switch (i % 8) {
                case 0: score += 1; break;
                case 1: score += 2; break;
                case 2: score += 3; break;
                case 3: score += 4; break;
                case 4: score -= 1; break;
                case 5: score -= 2; break;
                case 6: score -= 3; break;
            }
            try { score += b; } catch (RuntimeException e) { score = c; }
            if (score < 0) { score = 0; }
        }
        return score;
    }
}
