import { Bricolage_Grotesque, Instrument_Sans, IBM_Plex_Mono } from "next/font/google";
import StoreProvider from "./StoreProvider";
import AuthGate from "@/components/auth/AuthGate";
import AppShell from "@/components/layout/AppShell";
import "./globals.css";

const display = Bricolage_Grotesque({ subsets: ["latin"], variable: "--font-display", display: "swap" });
const sans = Instrument_Sans({ subsets: ["latin"], variable: "--font-sans", display: "swap" });
const mono = IBM_Plex_Mono({ subsets: ["latin"], weight: ["400", "500"], variable: "--font-mono", display: "swap" });

export const metadata = {
  title: "PersonalAI",
  description: "Local chatbot UI for Ollama"
};

export default function RootLayout({ children }) {
  return (
    <html lang="en" className={`${display.variable} ${sans.variable} ${mono.variable}`}>
      <body>
        <StoreProvider>
          <AuthGate>
            <AppShell>{children}</AppShell>
          </AuthGate>
        </StoreProvider>
      </body>
    </html>
  );
}
