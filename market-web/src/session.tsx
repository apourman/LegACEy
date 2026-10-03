import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { ApiError, getMe, sessionEnded, signIn, signOut, type Me } from './api';
import { browserCharacter, migrateStoredCharacter, writeCharacterCookie } from './character';

interface Session {
  me: Me | null; characterId: number | null; loading: boolean; error: string;
  selectCharacter: (id: number) => void; refresh: () => Promise<void>;
  login: (account: string, password: string) => Promise<void>; logout: () => Promise<void>;
}
/** The account as the server rendered it (the root loader) */
export interface InitialSession { me: Me | null; characterId: number | null; error: string }

const SessionContext = createContext<Session | null>(null);
export function useSession() {
  const value = useContext(SessionContext);
  if (!value) throw new Error('Session provider missing');
  return value;
}
/** Starts from the server's render, so the header is right before any script runs; from then on the browser keeps it current through /api/me */
export function SessionProvider({ initial, children }: { initial: InitialSession; children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(initial.me);
  const [characterId, setCharacterId] = useState<number | null>(initial.characterId);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(initial.error);
  const signedIn = useRef(initial.me !== null);
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
      setMe(account); setCharacterId(browserCharacter(account)); setError('');
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
    // a choice saved before the BFF moves into the cookie on the first load; that load may still show the server's choice briefly
    if (initial.me) {
      const saved = browserCharacter(initial.me);
      if (saved !== initial.characterId) setCharacterId(saved);
    }
    // the server had a session but couldn't load the account: try again from here
    if (initial.error) { setLoading(true); void refresh(); }
    return () => { sessionEnded.removeEventListener('ended', ended); document.removeEventListener('visibilitychange', visible); };
  }, [navigate]);
  function selectCharacter(id: number) {
    if (!me?.characters.some(c => c.id === id)) return;
    setCharacterId(id);
    migrateStoredCharacter(me);
    try { writeCharacterCookie(me.accountId, id); } catch { /* The selection still works for this page. */ }
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
