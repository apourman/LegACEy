import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { ApiError, getMe, sessionEnded, signIn, signOut, type Me } from './api';

interface Session {
  me: Me | null; characterId: number | null; loading: boolean; error: string;
  selectCharacter: (id: number) => void; refresh: () => Promise<void>;
  login: (account: string, password: string) => Promise<void>; logout: () => Promise<void>;
}
const SessionContext = createContext<Session | null>(null);
export function useSession() {
  const value = useContext(SessionContext);
  if (!value) throw new Error('Session provider missing');
  return value;
}
function readCharacter(me: Me): number | null {
  let saved: number | null = null;
  try { saved = Number(localStorage.getItem(`market-character-${me.accountId}`)); } catch { /* Storage can be disabled. */ }
  return me.characters.find(c => c.id === saved)?.id ?? me.characters[0]?.id ?? null;
}
export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [characterId, setCharacterId] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const signedIn = useRef(false);
  const generation = useRef(0);
  const navigate = useNavigate();
  const location = useLocation();
  const currentLocation = useRef(location);
  currentLocation.current = location;
  async function refresh(required = false) {
    const version = ++generation.current;
    try {
      const account = await getMe();
      if (version !== generation.current) {
        if (required) throw new ApiError('unauthorized', 401);
        return;
      }
      signedIn.current = true;
      setMe(account); setCharacterId(readCharacter(account)); setError('');
    } catch (e) {
      if (version === generation.current && !(e instanceof ApiError && e.status === 401)) setError(e instanceof Error ? e.message : 'Could not load your account.');
      if (required) throw e;
    } finally { if (version === generation.current) setLoading(false); }
  }
  useEffect(() => {
    const ended = () => {
      if (!signedIn.current) return;
      generation.current++;
      signedIn.current = false; setMe(null); setCharacterId(null); setLoading(false); setError('');
      const here = currentLocation.current;
      navigate('/signin', { state: { ended: true, returnTo: here.pathname + here.search } });
    };
    const visible = () => { if (document.visibilityState === 'visible' && signedIn.current) void refresh(); };
    sessionEnded.addEventListener('ended', ended);
    document.addEventListener('visibilitychange', visible);
    void refresh();
    return () => { sessionEnded.removeEventListener('ended', ended); document.removeEventListener('visibilitychange', visible); };
  }, [navigate]);
  function selectCharacter(id: number) {
    if (!me?.characters.some(c => c.id === id)) return;
    setCharacterId(id);
    try { localStorage.setItem(`market-character-${me.accountId}`, String(id)); } catch { /* The selection still works for this page. */ }
  }
  async function login(account: string, password: string) {
    generation.current++;
    signedIn.current = false;
    await signIn(account, password);
    await refresh(true);
  }
  async function logout() {
    generation.current++;
    await signOut(); generation.current++; signedIn.current = false; setMe(null); setCharacterId(null); setError(''); navigate('/');
  }
  return <SessionContext.Provider value={{ me, characterId, loading, error, selectCharacter, refresh, login, logout }}>{children}</SessionContext.Provider>;
}
