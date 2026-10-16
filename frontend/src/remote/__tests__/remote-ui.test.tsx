import '@testing-library/jest-dom/vitest';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import React from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { ConflictReason, TransferState } from '../../data/remote/remote-transfers';
import { RuntimeSystem, type DeviceRemoteStatusDTO, type DeviceTransferDTO, type SettingsDTO } from '../../data/sdk/model';
import { SettingsRemoteBank } from '../../settings/main/settings-remote-bank';
import { AppTheme } from '../../ui/app-theme';
import { FocusControlsProvider } from '../../ui/interaction/focus-controls/provider/focus-controls-provider';
import { FocusScope } from '../../ui/interaction/focus/scope/focus-scope';
import { RemoteStatusBadge } from '../remote-status-badge';

// jsdom does not have what Mantine uses to follow the screen
window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => { },
    removeEventListener: () => { },
    addListener: () => { },
    removeListener: () => { },
    dispatchEvent: () => false,
})) as typeof window.matchMedia;

globalThis.ResizeObserver ??= class {
    observe() { }
    unobserve() { }
    disconnect() { }
};

Element.prototype.scrollIntoView ??= () => { };

type Recorded = { method: string; path: string; search: string; body: string };

const state: { runtime: number; status: DeviceRemoteStatusDTO; requests: Recorded[] } = {
    runtime: RuntimeSystem.ANDROID,
    status: { mode: 'LOCAL', hasToken: false, reachable: false, authorized: true, transfers: [] },
    requests: [],
};

const record = async (request: Request) => {
    const url = new URL(request.url);
    state.requests.push({ method: request.method, path: url.pathname, search: url.search, body: await request.text() });
};

const server = setupServer(
    http.get('/api/settings', () => HttpResponse.json({ runtimeSystem: state.runtime } as SettingsDTO)),
    http.get('/api/device/status', () => HttpResponse.json(state.status)),
    http.put('/api/device/config', async ({ request }) => {
        await record(request.clone());
        const body = JSON.parse(await request.text()) as { serverUrl?: string; token?: string };
        state.status = { ...state.status, serverUrl: body.serverUrl, hasToken: state.status.hasToken || !!body.token };
        return HttpResponse.json(state.status);
    }),
    http.put('/api/device/mode', async ({ request }) => {
        await record(request.clone());
        const mode = new URL(request.url).searchParams.get('mode')!;
        if (state.status.serverUrl === 'https://down.example') {
            return new HttpResponse(null, { status: 503, headers: { 'error-message': JSON.stringify('The server is not reachable') } });
        }
        state.status = { ...state.status, mode, reachable: true };
        return HttpResponse.json(state.status);
    }),
    http.post('/api/device/test', async ({ request }) => {
        await record(request.clone());
        return HttpResponse.json({ version: '9.9.9', commitSeq: 3 });
    }),
    http.post('/api/device/transfers/:localId/resolve', async ({ request }) => {
        await record(request.clone());
        return HttpResponse.json({});
    }),
    http.post('/api/device/transfers/:localId/close', async ({ request }) => {
        await record(request.clone());
        return HttpResponse.json({});
    }),
    http.post('/api/device/sync', async ({ request }) => {
        await record(request.clone());
        return HttpResponse.json(state.status);
    }),
    // data reloaded after a change of bank
    http.get('*/api/*', () => HttpResponse.json({})),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
    cleanup();
    server.resetHandlers();
    state.requests = [];
    state.runtime = RuntimeSystem.ANDROID;
    state.status = { mode: 'LOCAL', hasToken: false, reachable: false, authorized: true, transfers: [] };
});
afterAll(() => server.close());

const renderUi = (ui: React.ReactNode) => {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity, refetchOnWindowFocus: false } },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            <AppTheme>
                <FocusControlsProvider>
                    <FocusScope id='test-root'>
                        {ui}
                    </FocusScope>
                </FocusControlsProvider>
            </AppTheme>
        </QueryClientProvider>,
    );
};

const transfer = (overrides: Partial<DeviceTransferDTO> = {}): DeviceTransferDTO => ({
    localId: 'transfer-1',
    saveId: 5,
    fileName: 'main.sav',
    state: TransferState.UPLOADED,
    localReloadPending: false,
    updatedAt: '2026-10-16T12:00:00',
    ...overrides,
});

describe('SettingsRemoteBank', () => {
    it('is not shown outside the Android app', async () => {
        state.runtime = RuntimeSystem.DOCKER;

        renderUi(<SettingsRemoteBank />);

        // let the settings load
        await new Promise(resolve => setTimeout(resolve, 50));
        expect(screen.queryByText('Remote bank')).not.toBeInTheDocument();
    });

    it('saves the address, without sending a token which was not typed', async () => {
        const user = userEvent.setup();
        renderUi(<SettingsRemoteBank />);

        const address = await screen.findByLabelText('Server address');
        await user.type(address, 'https://my-nas:3000');
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(state.requests).toHaveLength(1));
        expect(state.requests[ 0 ]).toMatchObject({ method: 'PUT', path: '/api/device/config' });
        expect(JSON.parse(state.requests[ 0 ]?.body ?? '')).toEqual({ serverUrl: 'https://my-nas:3000' });
        expect(await screen.findByText('Saved')).toBeInTheDocument();
    });

    it('sends the token when typed, and never shows a saved one', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, serverUrl: 'https://my-nas:3000', hasToken: true };
        renderUi(<SettingsRemoteBank />);

        const token = await screen.findByLabelText('Access token');
        expect(token).toHaveValue('');
        expect(token).toHaveAttribute('placeholder', expect.stringContaining('A token is saved'));

        await user.type(token, 's3cret');
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(state.requests).toHaveLength(1));
        expect(JSON.parse(state.requests[ 0 ]?.body ?? '')).toEqual({ serverUrl: 'https://my-nas:3000', token: 's3cret' });
        // the field is emptied once saved
        await waitFor(() => expect(token).toHaveValue(''));
    });

    it('warns when the token would travel unencrypted', async () => {
        const user = userEvent.setup();
        renderUi(<SettingsRemoteBank />);

        await user.type(await screen.findByLabelText('Server address'), 'http://192.168.1.10:3000');
        expect(screen.queryByText(/not encrypted/)).not.toBeInTheDocument();

        await user.type(screen.getByLabelText('Access token'), 'tok');

        expect(await screen.findByText(/not encrypted/)).toBeInTheDocument();
    });

    it('tests the saved connection only, and shows the server version', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, serverUrl: 'https://my-nas:3000' };
        renderUi(<SettingsRemoteBank />);

        const test = await screen.findByRole('button', { name: 'Test the connection' });
        await user.type(screen.getByLabelText('Server address'), '/more');
        // the address was edited: the test would not be about what is saved
        expect(test).toBeDisabled();

        await user.clear(screen.getByLabelText('Server address'));
        await user.type(screen.getByLabelText('Server address'), 'https://my-nas:3000');
        expect(test).toBeEnabled();
        await user.click(test);

        expect(await screen.findByText('Connected, PKVault server v9.9.9')).toBeInTheDocument();
    });

    it('switches to the server bank', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, serverUrl: 'https://my-nas:3000' };
        renderUi(<SettingsRemoteBank />);

        await user.click(await screen.findByText('Server'));

        await waitFor(() => expect(state.requests.find(r => r.path === '/api/device/mode')).toBeDefined());
        expect(state.requests.find(r => r.path === '/api/device/mode')!.search).toBe('?mode=REMOTE');
    });

    it('shows why the switch is refused', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, serverUrl: 'https://down.example' };
        renderUi(<SettingsRemoteBank />);

        await user.click(await screen.findByText('Server'));

        expect(await screen.findByText('The server is not reachable')).toBeInTheDocument();
    });
});

describe('RemoteStatusBadge', () => {
    it('is not shown when the remote bank is not used', async () => {
        renderUi(<RemoteStatusBadge />);

        await new Promise(resolve => setTimeout(resolve, 50));
        expect(screen.queryByRole('button')).not.toBeInTheDocument();
    });

    it('tells when the server is used and when it is offline', async () => {
        state.status = { ...state.status, mode: 'REMOTE', reachable: true };
        const { unmount } = renderUi(<RemoteStatusBadge />);
        expect(await screen.findByText('Remote bank')).toBeInTheDocument();
        unmount();

        state.status = { ...state.status, reachable: false, connectionError: 'No route to host' };
        renderUi(<RemoteStatusBadge />);
        expect(await screen.findByText('Server offline')).toBeInTheDocument();
    });

    it('opens the details with the transfers when clicked', async () => {
        const user = userEvent.setup();
        state.status = {
            ...state.status, mode: 'REMOTE', reachable: true, serverUrl: 'https://my-nas:3000',
            transfers: [ transfer({ state: TransferState.UPLOADED }) ],
        };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Remote bank'));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText('Using the bank of the server')).toBeInTheDocument();
        expect(within(dialog).getByText('https://my-nas:3000')).toBeInTheDocument();
        expect(within(dialog).getByText('main.sav')).toBeInTheDocument();
        expect(within(dialog).getByText('Open on the server')).toBeInTheDocument();
    });

    it('closes a save waiting for edits', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, mode: 'REMOTE', reachable: true, transfers: [ transfer() ] };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Remote bank'));
        await user.click(await screen.findByRole('button', { name: 'Stop working on this save on the server' }));

        await waitFor(() => expect(state.requests.find(r => r.path === '/api/device/transfers/transfer-1/close')).toBeDefined());
    });

    it('asks for a decision, and offers the choices of a save played since the upload', async () => {
        const user = userEvent.setup();
        state.status = {
            ...state.status, mode: 'REMOTE', reachable: true,
            transfers: [ transfer({ state: TransferState.CONFLICT, conflictReason: ConflictReason.DEVICE_SAVE_CHANGED }) ],
        };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Action needed'));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/you played since/)).toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Cancel the changes made on the server' })).toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Replace the save of this device' })).toBeInTheDocument();
        // nothing was decided without the user
        expect(state.requests).toHaveLength(0);
    });

    it('only offers to forget a delivery the server does not have anymore', async () => {
        const user = userEvent.setup();
        state.status = {
            ...state.status, mode: 'REMOTE', reachable: true,
            transfers: [ transfer({ state: TransferState.CONFLICT, conflictReason: ConflictReason.DELIVERY_GONE }) ],
        };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Action needed'));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByRole('button', { name: 'Forget these changes' })).toBeInTheDocument();
        expect(within(dialog).queryByRole('button', { name: 'Replace the save of this device' })).not.toBeInTheDocument();
        expect(within(dialog).queryByRole('button', { name: 'Cancel the changes made on the server' })).not.toBeInTheDocument();
    });

    it('shows a paused transfer with its reason, and a warning', async () => {
        const user = userEvent.setup();
        state.status = {
            ...state.status, mode: 'REMOTE', reachable: true,
            transfers: [
                transfer({ localId: 'a', state: TransferState.COMMITTED_ON_SERVER, lastError: 'Remote server unreachable' }),
                transfer({ localId: 'b', fileName: 'other.sav', state: TransferState.ACKED, warning: 'The server forgot this delivery' }),
            ],
        };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Action needed'));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/Paused: Remote server unreachable/)).toBeInTheDocument();
        expect(within(dialog).getByText('The server forgot this delivery')).toBeInTheDocument();
    });

    it('checks now on demand', async () => {
        const user = userEvent.setup();
        state.status = { ...state.status, mode: 'REMOTE', reachable: true };
        renderUi(<RemoteStatusBadge />);

        await user.click(await screen.findByText('Remote bank'));
        await user.click(await screen.findByRole('button', { name: 'Check now' }));

        await waitFor(() => expect(state.requests.find(r => r.path === '/api/device/sync')).toBeDefined());
    });
});
