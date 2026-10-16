import '@testing-library/jest-dom/vitest';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import React from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { AppTheme } from '../../../../ui/app-theme';
import { FocusControlsProvider } from '../../../../ui/interaction/focus-controls/provider/focus-controls-provider';
import { FocusScope } from '../../../../ui/interaction/focus/scope/focus-scope';
import { CopyToBankButton } from '../copy-to-bank-button';

// what the screen knows: the selected bank and the Pokémon of the save
const screenState: {
    bank: { id: string } | undefined;
    pkms: { id: string; canMoveToMain: boolean }[];
} = { bank: { id: 'bank-2' }, pkms: [] };

vi.mock('../../../../data/hooks/use-pkm-index', () => ({
    usePkmIndex: () => ({ data: screenState.pkms }),
}));

vi.mock('../../../bank/bank-context', () => ({
    BankContext: {
        useSelectedBankBoxes: () => ({ data: screenState.bank && { selectedBank: screenState.bank } }),
    },
}));

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

const requests: { method: string; path: string; body: string }[] = [];

const server = setupServer(
    http.put('*/api/storage/copy/pkm/bank', async ({ request }) => {
        requests.push({ method: request.method, path: new URL(request.url).pathname, body: await request.text() });
        return HttpResponse.json({});
    }),
    http.get('*/api/*', () => HttpResponse.json({})),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
    cleanup();
    server.resetHandlers();
    requests.length = 0;
    screenState.bank = { id: 'bank-2' };
    screenState.pkms = [];
});
afterAll(() => server.close());

const renderButton = (props: { pkmIds: string[]; saveId: number | null }) => {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity, refetchOnWindowFocus: false } },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            <AppTheme>
                <FocusControlsProvider>
                    <FocusScope id='test-root'>
                        <CopyToBankButton {...props} />
                    </FocusScope>
                </FocusControlsProvider>
            </AppTheme>
        </QueryClientProvider>,
    );
};

describe('CopyToBankButton', () => {
    it('copies the selected pokémon of the save to the selected bank', async () => {
        screenState.pkms = [ { id: 'p1', canMoveToMain: true }, { id: 'p2', canMoveToMain: true } ];
        const user = userEvent.setup();
        renderButton({ pkmIds: [ 'p1', 'p2' ], saveId: 7 });

        await user.click(screen.getByRole('button', { name: /Copy to bank/ }));

        await waitFor(() => expect(requests).toHaveLength(1));
        expect(requests[ 0 ]?.method).toBe('PUT');
        expect(JSON.parse(requests[ 0 ]?.body ?? '{}')).toEqual({ pkmIds: [ 'p1', 'p2' ], sourceSaveId: 7, bankId: 'bank-2' });
    });

    it('is disabled when one pokémon cannot go to the bank, and sends nothing', async () => {
        screenState.pkms = [ { id: 'p1', canMoveToMain: true }, { id: 'egg', canMoveToMain: false } ];
        const user = userEvent.setup();
        renderButton({ pkmIds: [ 'p1', 'egg' ], saveId: 7 });

        const button = screen.getByRole('button', { name: /Copy to bank/ });
        expect(button).toBeDisabled();
        await user.click(button);

        await new Promise(resolve => setTimeout(resolve, 50));
        expect(requests).toHaveLength(0);
    });

    it('is disabled without selected bank', () => {
        screenState.bank = undefined;
        screenState.pkms = [ { id: 'p1', canMoveToMain: true } ];
        renderButton({ pkmIds: [ 'p1' ], saveId: 7 });

        expect(screen.getByRole('button', { name: /Copy to bank/ })).toBeDisabled();
    });

    it('is not shown for a pokémon of the storage (only a save has something to copy)', () => {
        screenState.pkms = [ { id: 'p1', canMoveToMain: true } ];
        renderButton({ pkmIds: [ 'p1' ], saveId: null });

        expect(screen.queryByRole('button', { name: /Copy to bank/ })).not.toBeInTheDocument();
    });
});
